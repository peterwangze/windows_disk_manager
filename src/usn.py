"""Read NTFS change records using the ordinary Windows user token."""

from contextlib import contextmanager
import ctypes as ct
from ctypes import wintypes as wt
import os
import struct


QUERY_JOURNAL = 0x000900F4
READ_UNPRIVILEGED_JOURNAL = 0x000903AB
RECORD = struct.Struct("<IHH8s8sq8xI8xIHH")
ID_PADDING = bytes(8)
CLOSE = 0x80000000
SECURITY_OR_REPARSE = 0x00000800 | 0x00100000


class Unavailable(Exception):
    """The journal cannot establish continuity with the cached scan."""


class Journal:
    def __init__(self, volume, ioctl):
        self.volume, self.ioctl = volume, ioctl
        self.buffer = ct.create_string_buffer(1024 * 1024)

    def request(self, code, data=None):
        returned = wt.DWORD()
        if not self.ioctl(self.volume, code, data, len(data) if data else 0,
                          self.buffer, len(self.buffer), ct.byref(returned), None):
            raise Unavailable(f"Windows error {ct.get_last_error()}")
        return returned.value

    def position(self):
        if self.request(QUERY_JOURNAL) < 60:
            raise Unavailable("Incomplete NTFS journal information")
        ident, first, next_usn, lowest = struct.unpack_from("<8sqqq", self.buffer)
        minimum, maximum = struct.unpack_from("<HH", self.buffer, 56)
        if not minimum <= 2 <= maximum:
            raise Unavailable("Unsupported NTFS journal version")
        return ident, max(first, lowest), next_usn

    def page(self, ident, start):
        # NTFS V2 records have 64-bit references. The unprivileged API omits names.
        args = struct.pack("<qIIQQ8sHH4x", start, 0xFFFFFFFF, 0, 0, 0, ident, 2, 2)
        size = self.request(READ_UNPRIVILEGED_JOURNAL, args)
        if size < 8:
            raise Unavailable("Incomplete NTFS journal page")
        return struct.unpack_from("<q", self.buffer)[0], size

    def changes(self, ident, start, end, cancel):
        while start < end:
            cancel.check()
            next_usn, size = self.page(ident, start)
            if next_usn <= start:
                raise Unavailable("NTFS journal stopped before the expected position")
            offset = 8
            while offset < size:
                if size - offset < RECORD.size:
                    raise Unavailable("Incomplete NTFS change record")
                length, major, minor, fsid, parent, usn, reason, attrs, _, _ = RECORD.unpack_from(self.buffer, offset)
                if (length < RECORD.size or offset + length > size or major != 2 or minor != 0
                        or not start <= usn < next_usn):
                    raise Unavailable("Unexpected NTFS change record")
                if usn >= end:
                    break
                yield fsid + ID_PADDING, parent + ID_PADDING, reason, attrs
                offset += length
            start = next_usn


@contextmanager
def open_journal(root, open_file, invalid_handle, close_handle, device_io, get_drive_type):
    drive = os.path.splitdrive(root)[0]
    if len(drive) != 2 or get_drive_type(drive + "\\") not in (2, 3):
        raise Unavailable("No local volume change journal")
    # FILE_TRAVERSE permits the unprivileged control without raw volume read access.
    volume = open_file("\\\\.\\" + drive, 0x20, 7, None, 3, 0x02000000, None)
    if volume == invalid_handle:
        raise Unavailable(f"Windows error {ct.get_last_error()}")
    try:
        yield Journal(volume, device_io)
    finally:
        close_handle(volume)
