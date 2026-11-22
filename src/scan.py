"""Build the Windows disk usage tree snapshot. Run using 64-bit Python on Windows."""

import argparse
import ctypes as ct
from ctypes import wintypes as wt
from concurrent.futures import FIRST_COMPLETED, ProcessPoolExecutor, wait
from contextlib import contextmanager
from datetime import datetime
import itertools
import multiprocessing
import os
from pathlib import Path
import queue
import shutil
import sqlite3
import struct
import sys
import tempfile
import time


kernel = ct.WinDLL("kernel32", use_last_error=True)


def winfunc(name, result, *args):
    function = getattr(kernel, name)
    function.argtypes = args
    function.restype = result
    return function


create_file = winfunc("CreateFileW", wt.HANDLE, wt.LPCWSTR, wt.DWORD, wt.DWORD,
                      wt.LPVOID, wt.DWORD, wt.DWORD, wt.HANDLE)
get_info = winfunc("GetFileInformationByHandleEx", wt.BOOL, wt.HANDLE, ct.c_int,
                   wt.LPVOID, wt.DWORD)
close_handle = winfunc("CloseHandle", wt.BOOL, wt.HANDLE)
get_compressed_size = winfunc("GetCompressedFileSizeW", wt.DWORD, wt.LPCWSTR,
                              ct.POINTER(wt.DWORD))
get_drives = winfunc("GetLogicalDrives", wt.DWORD)
get_drive_type = winfunc("GetDriveTypeW", wt.UINT, wt.LPCWSTR)
set_error_mode = winfunc("SetErrorMode", wt.UINT, wt.UINT)
get_legacy_info = winfunc("GetFileInformationByHandle", wt.BOOL, wt.HANDLE, wt.LPVOID)

INVALID_HANDLE = ct.c_void_p(-1).value
DIRECTORY, SPARSE, REPARSE, COMPRESSED = 0x10, 0x200, 0x400, 0x800
WOF = 0x80000017
NAME_SURROGATE = 0x20000000
BUFFER_SIZE = 65536
HEADER = struct.Struct("<I36xqqII4xI16s")
FULL_HEADER = struct.Struct("<I36xqqIII")
EMPTY_ID = bytes(16)
INSERT_ENTRY = "INSERT INTO entries(parent,name,bytes,logical_bytes,flags,fsid) VALUES (?,?,?,?,?,?)"
INSERT_DIR = "INSERT INTO dirs VALUES (?,?,?,?,?,?,?,?,?)"
SCHEMA = """
CREATE TABLE dirs (
    id INTEGER PRIMARY KEY, parent INTEGER, name TEXT NOT NULL, bytes INTEGER NOT NULL,
    direct_bytes INTEGER NOT NULL, files INTEGER NOT NULL, flags INTEGER NOT NULL,
    logical_bytes INTEGER NOT NULL, fsid BLOB
);
CREATE TABLE entries (
    id INTEGER PRIMARY KEY, parent INTEGER NOT NULL, name TEXT NOT NULL, bytes INTEGER,
    logical_bytes INTEGER NOT NULL, flags INTEGER NOT NULL, fsid BLOB
);
CREATE TABLE volumes (
    root_id INTEGER PRIMARY KEY, total_bytes INTEGER, free_bytes INTEGER, started TEXT,
    finished TEXT, unique_bytes INTEGER, duplicate_files INTEGER, unknown_files INTEGER,
    serial INTEGER
);
"""


def now():
    return datetime.now().astimezone().isoformat(timespec="seconds")


def native(path):
    full = os.path.abspath(path)
    if full.startswith("\\\\?\\"):
        return full
    if full.startswith("\\\\"):
        return "\\\\?\\UNC\\" + full[2:]
    return "\\\\?\\" + full


@contextmanager
def handle(path, access):
    # OPEN_REPARSE_POINT, BACKUP_SEMANTICS, OPEN_NO_RECALL. All share modes.
    result = create_file(path, access, 7, None, 3, 0x02200000 | 0x00100000, None)
    if result == INVALID_HANDLE:
        raise ct.WinError(ct.get_last_error())
    try:
        yield result
    finally:
        close_handle(result)


def identity(file_handle):
    info = ct.create_string_buffer(24)
    if get_info(file_handle, 18, info, len(info)):
        serial, fsid = struct.unpack_from("<q16s", info)
        return serial, fsid if fsid != EMPTY_ID else None
    return None, None


def attributes(file_handle):
    info = ct.create_string_buffer(8)
    if get_info(file_handle, 9, info, len(info)):
        return struct.unpack_from("<II", info)
    legacy = ct.create_string_buffer(52)
    if not get_legacy_info(file_handle, legacy):
        raise ct.WinError(ct.get_last_error())
    return struct.unpack_from("<I", legacy)[0], 0


def children(directory, buffer):
    info_class = 19
    first = True
    while True:
        if not get_info(directory, info_class, buffer, len(buffer)):
            error = ct.get_last_error()
            if error in (18, 38):
                return
            if first and info_class == 19 and error in (1, 50, 87, 124):
                # FAT and third-party filesystem drivers may decline extended IDs.
                # The full-directory class still enumerates the same opened handle.
                info_class = 14
                continue
            raise ct.WinError(error)
        first = False
        offset = 0
        while True:
            if info_class == 19:
                following, logical, allocated, attr, n, tag, file_id = HEADER.unpack_from(buffer, offset)
                begin = offset + HEADER.size
                if file_id == EMPTY_ID:
                    file_id = None
            else:
                following, logical, allocated, attr, n, ea = FULL_HEADER.unpack_from(buffer, offset)
                begin = offset + FULL_HEADER.size
                tag, file_id = ea if attr & REPARSE else 0, None
            name = buffer[begin:begin+n].decode("utf-16-le", "replace")
            if name not in (".", ".."):
                yield name, logical, allocated, attr, tag, file_id
            if not following:
                break
            offset += following


def allocation(path, logical, raw_allocation, attr, tag):
    if attr & REPARSE and tag == WOF:
        # WOF FILE-provider files store their local compressed data in a named stream.
        # WIM-backed files need a different attribution model. Without a local backing
        # stream, leave the individual allocation unknown.
        try:
            with handle(path, 0x80) as file_handle:
                streams = ct.create_string_buffer(8192)
                while not get_info(file_handle, 7, streams, len(streams)):
                    if ct.get_last_error() not in (122, 234) or len(streams) >= 1024*1024:
                        return None
                    streams = ct.create_string_buffer(len(streams) * 2)
                offset = 0
                while True:
                    following, n, _, size = struct.unpack_from("<IIqq", streams, offset)
                    name = streams[offset+24:offset+24+n].decode("utf-16-le", "replace").casefold()
                    if name == ":wofcompresseddata:$data":
                        return size
                    if not following:
                        # Some WOF filter stacks expose only a regular, compressed
                        # default stream through the handle.
                        info = ct.create_string_buffer(24)
                        if get_info(file_handle, 1, info, len(info)):
                            exposed = struct.unpack_from("<q", info)[0]
                            if exposed > 0:
                                return exposed
                        return None
                    offset += following
        except OSError:
            return None
    if attr & (SPARSE | COMPRESSED):
        if attr & REPARSE:
            if tag & NAME_SURROGATE:
                return raw_allocation  # the directory record describes the link
            try:
                with handle(path, 0x80) as file_handle:
                    info = ct.create_string_buffer(16)
                    if get_info(file_handle, 8, info, len(info)):
                        return struct.unpack_from("<q", info)[0]
            except OSError:
                pass
            return None
        high = wt.DWORD()
        ct.set_last_error(0)
        value = get_compressed_size(path, ct.byref(high))
        if value == 0xFFFFFFFF and ct.get_last_error():
            return None
        return value | (high.value << 32)
    if logical and raw_allocation == 0 and not attr & REPARSE:
        # WOF can hide the reparse tag in both enumeration and handle queries while
        # returning an empty allocation in the directory and a real allocation on
        # the handle. A genuinely zero allocation remains zero.
        try:
            with handle(path, 0x80) as file_handle:
                info = ct.create_string_buffer(24)
                if get_info(file_handle, 1, info, len(info)):
                    return struct.unpack_from("<q", info)[0]
        except OSError:
            pass
        return None
    return raw_allocation


def connect(path):
    db = sqlite3.connect(path)
    db.executescript("""PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;
                        PRAGMA cache_size=-32768; PRAGMA temp_store=FILE;""")
    return db


class Directory:
    def __init__(self, ident, parent, path, name, flags=0, fsid=None):
        self.ident, self.parent, self.path, self.name = ident, parent, path, name
        self.flags, self.fsid = flags, fsid
        self.total = self.direct = self.files = self.logical = 0

    def include(self, child):
        self.total += child.total
        self.files += child.files
        self.logical += child.logical
        if child.flags & 5:
            self.flags |= 4
        if child.flags & 10:
            self.flags |= 8

    def row(self):
        return (self.ident, self.parent, self.name, self.total, self.direct, self.files,
                self.flags, self.logical, self.fsid)


class Cancelled(Exception):
    pass


_messages = None
_stop = None


def initialize_worker(messages, stop):
    global _messages, _stop
    _messages, _stop = messages, stop
    set_error_mode(0x8001)


def emit(*fields):
    print("\t".join(str(field).replace("\r", " ").replace("\n", " ").replace("\t", " ")
                    for field in fields), flush=True)


def report(*fields):
    if _messages is None:
        emit(*fields)
    else:
        _messages.put(fields)


class Cancellation:
    def __init__(self, path=None):
        self.path = path
        self.next_check = 0

    def check(self, force=False):
        current = time.monotonic()
        if force or current >= self.next_check:
            self.next_check = current + 0.2
            if (_stop is not None and _stop.is_set()) or (self.path and os.path.exists(self.path)):
                raise Cancelled()

    def interrupt_sql(self):
        try:
            self.check()
        except Cancelled:
            return 1
        return 0


def directory_entries(node, root, buffer):
    with handle(node.path, 1) as opened:
        attr, _ = attributes(opened)
        serial, fsid = identity(opened)
        if node is root:
            root.serial = serial
        if (node.fsid and fsid and node.fsid != fsid) or (
                root.serial is not None and serial is not None and serial != root.serial):
            node.flags |= 1
            node.fsid = None
            return
        if fsid is not None:
            node.fsid = fsid
        if attr & REPARSE:
            node.flags |= 2
            return
        # Enumerate exactly the handle checked above, including the fallback class.
        yield from children(opened, buffer)


def fallback_id(path, serial):
    if serial is not None:
        try:
            with handle(path, 0x80) as opened:
                actual_serial, fsid = identity(opened)
                if actual_serial == serial:
                    return fsid
        except OSError:
            pass
    return None


def scan_volume(root, index, stride, stage, ignored, cancel_file=None):
    started = now()
    shard = Path(stage) / f"volume-{index}.sqlite"
    counters = itertools.count(index + 1, stride)
    buffer = ct.create_string_buffer(BUFFER_SIZE)
    root_dir = Directory(next(counters), None, native(root), root)
    root_dir.serial = None
    # stack items with a parent reference allow postorder aggregation at any depth.
    stack = [(root_dir, None, False)]
    files = []
    dirs = []
    dir_count = file_count = unknown = 0
    next_report = 0
    cancel = Cancellation(cancel_file)

    def heartbeat():
        nonlocal next_report
        cancel.check()
        current = time.monotonic()
        if current >= next_report:
            report("SCAN", root, dir_count, file_count)
            next_report = current + 2

    db = connect(shard)
    try:
        db.set_progress_handler(cancel.interrupt_sql, 25000)
        db.executescript(SCHEMA)
        while stack:
            heartbeat()
            node, parent, done = stack.pop()
            if done or node.flags & 2:
                dirs.append(node.row())
                if parent is not None:
                    parent.include(node)
                dir_count += 1
            else:
                stack.append((node, parent, True))
                try:
                    for position, record in enumerate(directory_entries(node, root_dir, buffer)):
                        if position % 512 == 0:
                            heartbeat()
                        name, logical, raw_alloc, attr, tag, fsid = record
                        full = os.path.join(node.path, name)
                        if full.casefold() in ignored:
                            continue
                        if fsid is None:
                            fsid = fallback_id(full, root_dir.serial)
                        if attr & DIRECTORY:
                            flags = 2 if attr & REPARSE else 0
                            child = Directory(next(counters), node.ident, full, name, flags, fsid)
                            stack.append((child, node, False))
                            continue
                        amount = allocation(full, logical, raw_alloc, attr, tag)
                        flags = 2 if attr & REPARSE else 0
                        if amount is None or amount < 0:
                            amount = None
                            flags |= 1
                            node.flags |= 1
                            unknown += 1
                        else:
                            node.direct += amount
                            node.total += amount
                        node.logical += logical
                        node.files += 1
                        file_count += 1
                        files.append((node.ident, name, amount, logical, flags, fsid))
                        if len(files) >= 8192:
                            db.executemany(INSERT_ENTRY, files)
                            files.clear()
                except OSError:
                    node.flags |= 1
            if len(dirs) >= 4096:
                db.executemany(INSERT_DIR, dirs)
                dirs.clear()
        db.executemany(INSERT_ENTRY, files)
        db.executemany(INSERT_DIR, dirs)
        report("SCAN", root, dir_count, file_count)
        report("STAGE", f"正在汇总 {root} 的文件占用")
        finished = now()
        try:
            capacity = shutil.disk_usage(root)
            total_bytes, free_bytes = capacity.total, capacity.free
        except OSError:
            total_bytes = free_bytes = None
        unique, duplicates = db.execute("""
            SELECT coalesce(sum(a),0), coalesce(sum(n-1),0) FROM (
                SELECT max(bytes) AS a, count(*) AS n FROM entries
                GROUP BY coalesce(fsid, id)
            )
        """).fetchone()
        db.execute("INSERT INTO volumes VALUES (?,?,?,?,?,?,?,?,?)",
                   (root_dir.ident, total_bytes, free_bytes, started, finished, unique, duplicates,
                    unknown, root_dir.serial))
        db.commit()
    except sqlite3.OperationalError:
        cancel.check(force=True)
        raise
    finally:
        db.close()
    return str(shard)


def refresh_capacity(output):
    # Reflect normal volume usage after the final database is in place and the
    # temporary per-volume shards and index-sort files have been released.
    db = sqlite3.connect(output, timeout=20)
    try:
        roots = db.execute("SELECT id, name FROM dirs WHERE parent IS NULL").fetchall()
        with db:
            for ident, root in roots:
                try:
                    capacity = shutil.disk_usage(root)
                except OSError:
                    continue
                db.execute("UPDATE volumes SET total_bytes=?, free_bytes=? WHERE root_id=?",
                           (capacity.total, capacity.free, ident))
    finally:
        db.close()


def mounted_roots():
    drives = get_drives()
    if not drives:
        raise ct.WinError(ct.get_last_error())
    roots = []
    for i in range(26):
        root = chr(65 + i) + ":\\"
        if drives & (1 << i) and get_drive_type(root) > 1:
            try:
                shutil.disk_usage(root)
            except OSError:
                continue
            roots.append(root)
    if not roots:
        raise RuntimeError("没有可读取的已挂载磁盘。")
    return roots


def drain(messages):
    while True:
        try:
            event = messages.get_nowait()
        except queue.Empty:
            break
        emit(*event)


def scan_roots(roots, stage, ignored, cancel):
    if len(roots) == 1:
        return [scan_volume(roots[0], 0, 1, stage, ignored, cancel.path)]
    ctx = multiprocessing.get_context("spawn")
    messages, stop = ctx.Queue(), ctx.Event()
    try:
        with ProcessPoolExecutor(max_workers=min(len(roots), 4), mp_context=ctx,
                                 initializer=initialize_worker, initargs=(messages, stop)) as pool:
            jobs = []
            try:
                for i, root in enumerate(roots):
                    jobs.append(pool.submit(scan_volume, root, i, len(roots), stage, ignored))
                pending = set(jobs)
                while pending:
                    cancel.check(force=True)
                    completed, pending = wait(pending, timeout=0.25, return_when=FIRST_COMPLETED)
                    drain(messages)
                    for job in completed:
                        job.result()
            finally:
                stop.set()
                for job in jobs:
                    job.cancel()
        drain(messages)
        return [job.result() for job in jobs]
    finally:
        messages.close()
        messages.join_thread()


def run(output, exclude=None, root=None, cancel_file=None):
    set_error_mode(0x8001)
    cancel = Cancellation(cancel_file)
    cancel.check(force=True)
    emit("STAGE", "正在识别磁盘和准备目录索引")
    if root is None:
        roots = mounted_roots()
    else:
        root = os.path.abspath(root)
        if not os.lstat(root).st_file_attributes & DIRECTORY:
            raise ValueError("扫描位置必须是文件夹。")
        roots = [root]
    output = Path(output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    if exclude and root and Path(root).is_relative_to(Path(exclude).absolute()):
        raise ValueError("扫描位置位于排除目录内。")
    with tempfile.TemporaryDirectory(dir=output.parent, prefix=".scan-") as stage:
        ignored = {native(p).casefold() for p in
                   (stage, output, exclude, cancel_file, getattr(sys, "_MEIPASS", None)) if p}
        shards = scan_roots(roots, stage, ignored, cancel)
        cancel.check(force=True)
        emit("STAGE", "正在合并各磁盘的扫描结果")
        result = Path(stage) / "report.sqlite"
        db = connect(result)
        try:
            db.set_progress_handler(cancel.interrupt_sql, 25000)
            db.executescript(SCHEMA)
            for shard in shards:
                db.execute("ATTACH DATABASE ? AS source", (shard,))
                with db:
                    db.execute("INSERT INTO dirs SELECT * FROM source.dirs")
                    db.execute("""INSERT INTO entries(parent,name,bytes,logical_bytes,flags,fsid)
                                  SELECT parent,name,bytes,logical_bytes,flags,fsid FROM source.entries""")
                    db.execute("INSERT INTO volumes SELECT * FROM source.volumes")
                db.execute("DETACH DATABASE source")
                Path(shard).unlink()
            emit("STAGE", "正在建立文件树排序索引")
            db.executescript("""
                CREATE INDEX dirs_parent_bytes ON dirs(parent, bytes DESC, name);
                CREATE INDEX entries_parent_bytes ON entries(parent, bytes DESC, name);
            """)
            emit("STAGE", "正在校验目录汇总和数据库完整性")
            expected = db.execute("SELECT sum(files),sum(bytes),sum(logical_bytes) FROM dirs WHERE parent IS NULL").fetchone()
            actual = db.execute("SELECT count(*),coalesce(sum(bytes),0),coalesce(sum(logical_bytes),0) FROM entries").fetchone()
            if expected != actual or db.execute("PRAGMA quick_check").fetchone() != ("ok",):
                raise RuntimeError("Snapshot consistency check failed.")
        except sqlite3.OperationalError:
            cancel.check(force=True)
            raise
        finally:
            db.close()
        with result.open("rb+") as file:
            os.fsync(file.fileno())
        cancel.check(force=True)
        os.replace(result, output)
    try:
        refresh_capacity(output)
    except (OSError, sqlite3.Error):
        # The complete, validated snapshot already contains per-volume readings.
        pass
    emit("DONE", output)


if __name__ == "__main__":
    # Frozen workers unpickle functions from this module. Definitions must exist
    # before dispatch, while argument parsing and all I/O must happen afterwards.
    multiprocessing.freeze_support()
    sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default=Path(__file__).resolve().parents[1] / "disk-usage.sqlite")
    parser.add_argument("--exclude", help="Application data directory to omit from the scan")
    parser.add_argument("--root", help="Scan only this directory (instead of all mounted drives)")
    parser.add_argument("--cancel-file", help="Cancel when this file appears")
    args = parser.parse_args()
    try:
        run(args.output, args.exclude, args.root, args.cancel_file)
    except (Cancelled, KeyboardInterrupt):
        emit("CANCELLED")
        sys.exit(2)
    except Exception as error:
        emit("ERROR", str(error))
        sys.exit(1)
