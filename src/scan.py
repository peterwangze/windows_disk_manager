"""Build the Windows disk usage tree snapshot. Run using 64-bit Python on Windows."""

import argparse
import ctypes as ct
from ctypes import wintypes as wt
from concurrent.futures import FIRST_COMPLETED, ProcessPoolExecutor, wait
from contextlib import closing, contextmanager
from datetime import datetime
import itertools
import json
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

import usn


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
get_volume_info = winfunc("GetVolumeInformationW", wt.BOOL, wt.LPCWSTR, wt.LPWSTR, wt.DWORD,
                          wt.LPVOID, wt.LPVOID, wt.LPVOID, wt.LPWSTR, wt.DWORD)
set_error_mode = winfunc("SetErrorMode", wt.UINT, wt.UINT)
get_legacy_info = winfunc("GetFileInformationByHandle", wt.BOOL, wt.HANDLE, wt.LPVOID)
device_io = winfunc("DeviceIoControl", wt.BOOL, wt.HANDLE, wt.DWORD, wt.LPVOID,
                    wt.DWORD, wt.LPVOID, wt.DWORD, ct.POINTER(wt.DWORD), wt.LPVOID)

INVALID_HANDLE = ct.c_void_p(-1).value
DIRECTORY, SPARSE, REPARSE, COMPRESSED = 0x10, 0x200, 0x400, 0x800
WOF = 0x80000017
NAME_SURROGATE = 0x20000000
BUFFER_SIZE = 65536
HEADER = struct.Struct("<I36xqqII4xI16s")
FULL_HEADER = struct.Struct("<I36xqqIII")
EMPTY_ID = bytes(16)
INSERT_ENTRY = "INSERT INTO entries(parent,name,bytes,logical_bytes,flags,fsid,root_id) VALUES (?,?,?,?,?,?,?)"
INSERT_DIR = "INSERT INTO dirs VALUES (?,?,?,?,?,?,?,?,?,?,?)"
SNAPSHOT_VERSION = 1
SCHEMA = """
CREATE TABLE dirs (
    id INTEGER PRIMARY KEY, parent INTEGER, name TEXT NOT NULL, bytes INTEGER NOT NULL,
    direct_bytes INTEGER NOT NULL, files INTEGER NOT NULL, flags INTEGER NOT NULL,
    logical_bytes INTEGER NOT NULL, fsid BLOB, root_id INTEGER NOT NULL, scan_error INTEGER NOT NULL
);
CREATE TABLE entries (
    id INTEGER PRIMARY KEY, parent INTEGER NOT NULL, name TEXT NOT NULL, bytes INTEGER,
    logical_bytes INTEGER NOT NULL, flags INTEGER NOT NULL, fsid BLOB, root_id INTEGER NOT NULL
);
CREATE TABLE volumes (
    root_id INTEGER PRIMARY KEY, total_bytes INTEGER, free_bytes INTEGER, started TEXT,
    finished TEXT, unique_bytes INTEGER, duplicate_files INTEGER, unknown_files INTEGER,
    serial INTEGER
);
CREATE TABLE checkpoints (
    root_id INTEGER PRIMARY KEY, journal BLOB NOT NULL, position INTEGER NOT NULL, scope TEXT NOT NULL
);
CREATE TABLE pending_changes (
    root_id INTEGER NOT NULL, fsid BLOB NOT NULL, parent_fsid BLOB NOT NULL,
    reason INTEGER NOT NULL, attrs INTEGER NOT NULL, PRIMARY KEY(root_id, fsid)
) WITHOUT ROWID;
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


def supports_parallel_reads(root):
    drive = os.path.splitdrive(root)[0]
    if len(drive) != 2 or get_drive_type(drive + "\\") != 3:
        return False
    volume = create_file("\\\\.\\" + drive, 0, 7, None, 3, 0, None)
    if volume == INVALID_HANDLE:
        return False
    try:
        # IOCTL_STORAGE_QUERY_PROPERTY / StorageDeviceSeekPenaltyProperty.
        query = struct.pack("<III", 7, 0, 0)
        descriptor = ct.create_string_buffer(64)
        returned = wt.DWORD()
        ok = device_io(volume, 0x002D1400, query, len(query), descriptor, len(descriptor),
                       ct.byref(returned), None)
        return bool(ok and returned.value >= 9 and descriptor.raw[8] == 0)
    finally:
        close_handle(volume)


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
    def __init__(self, ident, parent, path, name, flags=0, fsid=None, root_id=None):
        self.ident, self.parent, self.path, self.name = ident, parent, path, name
        self.flags, self.fsid = flags, fsid
        self.root_id = ident if root_id is None else root_id
        self.scan_error = 0
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
                self.flags, self.logical, self.fsid, self.root_id, self.scan_error)


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
            node.scan_error = 1
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


def file_values(path, logical, raw_alloc, attr, tag, fsid):
    amount = raw_alloc
    if attr & (SPARSE | COMPRESSED | REPARSE) or (logical and raw_alloc == 0):
        amount = allocation(path, logical, raw_alloc, attr, tag)
    flags = 2 if attr & REPARSE else 0
    if amount is None or amount < 0:
        amount = None
        flags |= 1
    return amount, logical, flags, fsid


def write_forest(db, trees, root_dir, counters, ignored, cancel, progress, deferred=None, spine=None):
    buffer = ct.create_string_buffer(BUFFER_SIZE)
    stack = [(tree, None, 0, False) for tree in trees]
    excluded_names = {}
    for path in ignored:
        parent, name = os.path.split(path)
        excluded_names.setdefault(parent, set()).add(name)
    files = []
    dirs = []
    dir_count = file_count = unknown = 0
    next_report = 0

    def heartbeat():
        nonlocal next_report
        cancel.check()
        current = time.monotonic()
        if current >= next_report:
            progress(dir_count, file_count)
            next_report = current + 1

    while stack:
        node, parent, depth, done = stack.pop()
        if done or node.flags & 2:
            if spine is not None:
                spine[node.ident] = node
            dirs.append(node.row())
            if parent is not None:
                parent.include(node)
            dir_count += 1
        else:
            heartbeat()
            omitted = excluded_names.get(node.path.casefold())
            prefix = node.path if node.path.endswith("\\") else node.path + "\\"
            stack.append((node, parent, depth, True))
            try:
                for position, record in enumerate(directory_entries(node, root_dir, buffer)):
                    if position and position % 512 == 0:
                        heartbeat()
                    name, logical, raw_alloc, attr, tag, fsid = record
                    if omitted and name.casefold() in omitted:
                        continue
                    if fsid is None:
                        fsid = fallback_id(prefix + name, root_dir.serial)
                    if attr & DIRECTORY:
                        flags = 2 if attr & REPARSE else 0
                        child = Directory(next(counters), node.ident, prefix + name, name,
                                          flags, fsid, root_dir.ident)
                        if deferred is not None and not flags and (depth >= 2 or len(stack) >= 512):
                            deferred.append(child)
                        else:
                            stack.append((child, node, depth + 1, False))
                        continue
                    amount, logical, flags, fsid = file_values(prefix + name, logical, raw_alloc, attr, tag, fsid)
                    if amount is None:
                        node.flags |= 1
                        unknown += 1
                    else:
                        node.direct += amount
                        node.total += amount
                    node.logical += logical
                    node.files += 1
                    file_count += 1
                    files.append((node.ident, name, amount, logical, flags, fsid, root_dir.ident))
                    if len(files) >= 8192:
                        db.executemany(INSERT_ENTRY, files)
                        files.clear()
            except OSError:
                node.flags |= 1
                node.scan_error = 1
        if len(dirs) >= 4096:
            db.executemany(INSERT_DIR, dirs)
            dirs.clear()
    db.executemany(INSERT_ENTRY, files)
    db.executemany(INSERT_DIR, dirs)
    progress(dir_count, file_count)
    return dir_count, file_count, unknown


def merge_shards(db, others):
    for path in others:
        db.execute("ATTACH DATABASE ? AS source", (str(path),))
        with db:
            db.execute("INSERT INTO dirs SELECT * FROM source.dirs")
            db.execute("""INSERT INTO entries(parent,name,bytes,logical_bytes,flags,fsid,root_id)
                          SELECT parent,name,bytes,logical_bytes,flags,fsid,root_id FROM source.entries""")
            db.execute("INSERT INTO volumes SELECT * FROM source.volumes")
            db.execute("INSERT INTO checkpoints SELECT * FROM source.checkpoints")
            db.execute("INSERT INTO pending_changes SELECT * FROM source.pending_changes")
        db.execute("DETACH DATABASE source")
        Path(path).unlink()


def scan_branches(slot, path, trees, root_dir, first_id, stride, ignored):
    # Each child owns one SQLite file and a disjoint directory ID sequence.
    cancel = Cancellation()
    db = connect(path)
    try:
        db.set_progress_handler(cancel.interrupt_sql, 25000)
        db.executescript(SCHEMA)

        def progress(dirs, files):
            report(slot, dirs, files)

        counts = write_forest(db, trees, root_dir, itertools.count(first_id, stride), ignored, cancel, progress)
        db.commit()
    finally:
        db.close()
    return trees, counts


def scan_partitions(trees, root_dir, first_id, stride, workers, stage, index, ignored, cancel, progress):
    # More batches than workers let an idle worker take the next group of subtrees.
    groups = min(workers * 4, len(trees))
    totals = {}
    paths, tasks = [], []

    def on_progress(slot, dirs, files):
        totals[slot] = (dirs, files)
        progress(sum(d for d, f in totals.values()), sum(f for d, f in totals.values()))

    for slot in range(groups):
        path = Path(stage) / f"branch-{index}-{slot}.sqlite"
        paths.append(path)
        tasks.append((slot, path, trees[slot::groups], root_dir, first_id + slot * stride,
                      stride * groups, ignored))
    return paths, run_workers(scan_branches, tasks, workers, cancel, on_progress)


def change_journal(root):
    return usn.open_journal(root, create_file, INVALID_HANDLE, close_handle, device_io, get_drive_type)


def journal_identity(root):
    drive_root = os.path.splitdrive(root)[0] + "\\"
    filesystem = ct.create_unicode_buffer(16)
    if not get_volume_info(drive_root, None, 0, None, None, None, filesystem, len(filesystem)) or filesystem.value != "NTFS":
        raise usn.Unavailable("The scan root is not on an NTFS drive")
    with handle(native(root), 0x80) as opened:
        if attributes(opened)[0] & REPARSE:
            raise usn.Unavailable("The scan root is a reparse point")
        serial, fsid = identity(opened)
    with handle(native(drive_root), 0x80) as drive:
        if serial is None or not fsid or fsid[8:] != usn.ID_PADDING or serial != identity(drive)[0]:
            raise usn.Unavailable("The scan root does not match the journal volume")
    return serial, fsid


def baseline_position(root):
    try:
        with change_journal(root) as journal:
            ident, _, position = journal.position()
            journal.page(ident, position)
            return ident, position, *journal_identity(root)
    except (OSError, usn.Unavailable):
        return None


def scan_volume(root, index, stride, stage, ignored, scope, cancel_file=None, base_id=0):
    started = now()
    shard_index = base_id + index
    shard = Path(stage) / f"volume-{shard_index}.sqlite"
    counters = itertools.count(base_id + index + 1, stride)
    root_dir = Directory(next(counters), None, native(root), root)
    root_dir.serial = None
    cancel = Cancellation(cancel_file)
    budget = min(6, max(1, (os.cpu_count() or 2) // 2))
    workers = max(1, min(3, budget // min(stride, 4)))
    deferred, spine = ([], {}) if workers > 1 and supports_parallel_reads(root) else (None, None)

    def progress(dirs, files):
        report("SCAN", root, dirs, files)

    checkpoint = baseline_position(root)
    db = connect(shard)
    try:
        db.set_progress_handler(cancel.interrupt_sql, 25000)
        db.executescript(SCHEMA)
        first_dirs, first_files, unknown = write_forest(db, [root_dir], root_dir, counters,
                                                        ignored, cancel, progress, deferred, spine)
        if deferred:
            def branch_progress(dirs, files):
                progress(first_dirs + dirs, first_files + files)

            if len(deferred) >= 48:
                db.commit()
                paths, batches = scan_partitions(deferred, root_dir, next(counters), stride, workers,
                                                 stage, shard_index, ignored, cancel, branch_progress)
                report("STAGE", f"正在合并 {root} 的目录统计")
                merge_shards(db, paths)
            else:
                counts = write_forest(db, deferred, root_dir, counters, ignored, cancel, branch_progress)
                batches = [(deferred, counts)]
            for trees, counts in batches:
                unknown += counts[2]
                for tree in trees:
                    ancestor = tree.parent
                    while ancestor is not None:
                        spine[ancestor].include(tree)
                        ancestor = spine[ancestor].parent
            db.executemany("UPDATE dirs SET bytes=?, files=?, flags=?, logical_bytes=? WHERE id=?",
                           ((n.total, n.files, n.flags, n.logical, n.ident) for n in spine.values()))
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
        if (checkpoint and checkpoint[2:] == (root_dir.serial, root_dir.fsid)
                and not root_dir.flags & 2
                and not db.execute("SELECT 1 FROM dirs WHERE fsid IS NULL AND flags & 2 = 0 LIMIT 1").fetchone()):
            db.execute("INSERT INTO checkpoints VALUES (?,?,?,?)", (root_dir.ident, *checkpoint[:2], scope))
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


def run_workers(work, tasks, workers, cancel, receive):
    ctx = multiprocessing.get_context("spawn")
    messages, stop = ctx.Queue(), ctx.Event()

    def drain():
        while True:
            try:
                event = messages.get_nowait()
            except queue.Empty:
                return
            receive(*event)

    try:
        with ProcessPoolExecutor(max_workers=workers, mp_context=ctx, initializer=initialize_worker,
                                 initargs=(messages, stop)) as pool:
            jobs = []
            try:
                for task in tasks:
                    cancel.check()
                    jobs.append(pool.submit(work, *task))
                pending = set(jobs)
                while pending:
                    cancel.check(force=True)
                    completed, pending = wait(pending, timeout=0.15, return_when=FIRST_COMPLETED)
                    drain()
                    for job in completed:
                        job.result()
            finally:
                stop.set()
                for job in jobs:
                    job.cancel()
        drain()
        return [job.result() for job in jobs]
    finally:
        messages.close()
        messages.join_thread()


def scan_roots(roots, stage, ignored, scopes, cancel, base_id=0):
    if len(roots) == 1:
        root = roots[0]
        return [scan_volume(root, 0, 1, stage, ignored, scopes[root], cancel.path, base_id)]
    tasks = [(root, i, len(roots), stage, ignored, scopes[root], None, base_id) for i, root in enumerate(roots)]
    return run_workers(scan_volume, tasks, min(len(roots), 4), cancel, emit)


def scan_scope(root, ignored, elevated):
    prefix = native(root).casefold().rstrip("\\") + "\\"
    # A run's scratch files are always excluded. Persist only stable exclusions
    # inside this root so relocating the output outside the tree is harmless.
    return json.dumps([elevated, sorted(p for p in ignored if p.startswith(prefix))])


class VolumeRefresh:
    def __init__(self, db, root, root_id, fsid, serial, scope, ignored, next_id, cancel):
        self.db, self.root, self.cancel = db, root, cancel
        self.root_dir = Directory(root_id, None, native(root), root, fsid=fsid)
        self.root_dir.serial = serial
        self.scope, self.ignored = scope, ignored
        self.ids = next_id
        self.locations = {root_id: (0, self.root_dir.path, None, root, fsid)}
        self.buffer = ct.create_string_buffer(BUFFER_SIZE)

    def location(self, directory):
        chain = []
        while directory not in self.locations:
            parent, name, fsid = self.db.execute("SELECT parent,name,fsid FROM dirs WHERE id=?", (directory,)).fetchone()
            chain.append((directory, parent, name, fsid))
            directory = parent
        depth, path, *_ = self.locations[directory]
        for ident, parent, name, fsid in reversed(chain):
            depth, path = depth + 1, os.path.join(path, name)
            self.locations[ident] = (depth, path, parent, name, fsid)
        return self.locations[chain[0][0] if chain else directory]

    def journal_changes(self, checkpoint):
        ident, start, scope = checkpoint
        root_id = self.root_dir.ident
        if scope != self.scope or journal_identity(self.root) != (self.root_dir.serial, self.root_dir.fsid):
            raise usn.Unavailable("The scan root or exclusions have changed")
        pending = {fsid: (parent, reason, attrs) for fsid, parent, reason, attrs in self.db.execute(
            "SELECT fsid,parent_fsid,reason,attrs FROM pending_changes WHERE root_id=?", (root_id,))}
        parents = {parent for parent, _, _ in pending.values()}
        changed = {fsid for fsid, (_, _, attrs) in pending.items() if not attrs & DIRECTORY}
        rebuild = {fsid for fsid, (_, reason, attrs) in pending.items()
                   if attrs & DIRECTORY and reason & usn.SECURITY_OR_REPARSE}
        with change_journal(self.root) as journal:
            current, first, end = journal.position()
            if current != ident or not first <= start <= end:
                raise usn.Unavailable("The saved position is outside the current NTFS journal")
            for fsid, parent, reason, attrs in journal.changes(ident, start, end, self.cancel):
                parents.add(parent)
                if not attrs & DIRECTORY:
                    changed.add(fsid)
                elif reason & usn.SECURITY_OR_REPARSE:
                    rebuild.add(fsid)
                if reason & usn.CLOSE:
                    pending.pop(fsid, None)
                else:
                    pending[fsid] = (parent, reason, attrs)
        if self.root_dir.fsid in rebuild:
            raise usn.Unavailable("Access or reparse metadata changed at the scan root")
        return end, parents, changed, rebuild, pending

    def remove_tree(self, directory):
        subtree = """WITH RECURSIVE tree(id) AS (
            VALUES (?) UNION ALL SELECT d.id FROM dirs d JOIN tree ON d.parent=tree.id
        )"""
        self.db.execute(subtree + " DELETE FROM entries WHERE parent IN (SELECT id FROM tree)", (directory,))
        self.db.execute(subtree + " DELETE FROM dirs WHERE id IN (SELECT id FROM tree)", (directory,))

    def refresh_directory(self, ident, rebuild):
        _, path, parent, name, fsid = self.location(ident)
        root_id = self.root_dir.ident
        node = Directory(ident, parent, path, name, fsid=fsid, root_id=root_id)
        files = {row[1]: (row[0], row[2:]) for row in self.db.execute(
            "SELECT id,name,bytes,logical_bytes,flags,fsid FROM entries WHERE parent=?", (ident,))}
        folders = {row[1]: (row[0], row[2], row[3] & 2) for row in self.db.execute(
            "SELECT id,name,fsid,flags FROM dirs WHERE parent=?", (ident,))}
        added, removed, trees = [], [], []
        try:
            for name, logical, raw_alloc, attr, tag, file_id in directory_entries(node, self.root_dir, self.buffer):
                self.cancel.check()
                child = os.path.join(path, name)
                if child.casefold() in self.ignored:
                    continue
                if file_id is None:
                    file_id = fallback_id(child, self.root_dir.serial)
                if attr & DIRECTORY:
                    flags = 2 if attr & REPARSE else 0
                    existing = folders.get(name)
                    if existing and existing[1:] == (file_id, flags) and file_id not in rebuild:
                        del folders[name]
                    else:
                        trees.append(Directory(next(self.ids), ident, child, name, flags, file_id, root_id))
                    continue
                values = file_values(child, logical, raw_alloc, attr, tag, file_id)
                old = files.pop(name, None)
                if old is None or old[1] != values:
                    if old:
                        removed.append((old[0],))
                    added.append((ident, name, *values, root_id))
        except OSError:
            node.scan_error = 1
        if node.flags & 2 or node.fsid != fsid:
            raise usn.Unavailable("A directory changed identity during the refresh")
        self.db.executemany("DELETE FROM entries WHERE id=?", itertools.chain(removed, ((r[0],) for r in files.values())))
        self.db.executemany(INSERT_ENTRY, added)
        for directory, _, _ in folders.values():
            self.cancel.check()
            self.remove_tree(directory)
        if trees:
            write_forest(self.db, trees, self.root_dir, self.ids, self.ignored, self.cancel, lambda *_: None)
        self.db.execute("UPDATE dirs SET scan_error=? WHERE id=?", (node.scan_error, ident))

    def refresh_totals(self, directories):
        for ident in sorted(directories, key=lambda d: self.location(d)[0], reverse=True):
            self.cancel.check()
            direct, files, logical, unknown = self.db.execute("""
                SELECT coalesce(sum(bytes),0),count(*),coalesce(sum(logical_bytes),0),count(*)-count(bytes)
                FROM entries WHERE parent=?
            """, (ident,)).fetchone()
            allocated, child_files, child_logical, partial, links = self.db.execute("""
                SELECT coalesce(sum(bytes),0),coalesce(sum(files),0),coalesce(sum(logical_bytes),0),
                       coalesce(max((flags & 5) != 0),0),coalesce(max((flags & 10) != 0),0)
                FROM dirs WHERE parent=?
            """, (ident,)).fetchone()
            self.db.execute("""
                UPDATE dirs SET bytes=?, direct_bytes=?, files=?, logical_bytes=?,
                                flags=(scan_error OR ?) | ? WHERE id=?
            """, (direct + allocated, direct, files + child_files, logical + child_logical,
                  bool(unknown), 4 * partial | 8 * links, ident))

    def file_totals(self, schema):
        # Only groups with an inserted or deleted row can change the volume totals.
        return self.db.execute(f"""
            SELECT coalesce(sum(allocated),0),coalesce(sum(max(n-1,0)),0),coalesce(sum(unknown),0)
            FROM (
                SELECT max(e.bytes) AS allocated,count(*) AS n,count(*)-count(e.bytes) AS unknown
                FROM changed_files c CROSS JOIN {schema}.entries e ON e.root_id=? AND e.fsid=c.fsid
                WHERE c.entry_id=0 GROUP BY c.fsid
                UNION ALL
                SELECT e.bytes,1,e.bytes IS NULL FROM changed_files c
                CROSS JOIN {schema}.entries e ON e.id=c.entry_id AND e.root_id=? AND e.fsid IS NULL
                WHERE c.entry_id!=0
            )
        """, (self.root_dir.ident, self.root_dir.ident)).fetchone()

    def run(self, checkpoint):
        started = now()
        end, parents, changed, rebuild, pending = self.journal_changes(checkpoint)
        root_id = self.root_dir.ident
        dirty, totals = set(), set()
        for fsid in parents:
            self.cancel.check()
            dirty.update(ident for ident, in self.db.execute(
                "SELECT id FROM dirs WHERE root_id=? AND fsid=? AND flags & 2 = 0", (root_id, fsid)))
        for fsid in changed:
            self.cancel.check()
            dirty.update(ident for ident, in self.db.execute(
                "SELECT parent FROM entries WHERE root_id=? AND fsid=?", (root_id, fsid)))
        for fsid in rebuild:
            dirty.update(ident for ident, in self.db.execute(
                "SELECT parent FROM dirs WHERE root_id=? AND fsid=? AND parent IS NOT NULL", (root_id, fsid)))
        emit("STAGE", f"正在增量刷新 {self.root}（{len(dirty)} 个待检查目录）")
        emit("DELTA", self.root, 0, len(dirty))
        next_report = time.monotonic() + 1
        for index, ident in enumerate(sorted(dirty, key=lambda d: self.location(d)[0]), 1):
            self.cancel.check()
            # An earlier update may already have removed or rebuilt this subtree.
            if self.db.execute("SELECT 1 FROM dirs WHERE id=?", (ident,)).fetchone():
                self.refresh_directory(ident, rebuild)
                while ident is not None and ident not in totals:
                    totals.add(ident)
                    ident = self.location(ident)[2]
            if time.monotonic() >= next_report or index == len(dirty):
                emit("DELTA", self.root, index, len(dirty))
                next_report = time.monotonic() + 1
        self.refresh_totals(totals)
        before, after = self.file_totals("baseline"), self.file_totals("main")
        unique, duplicates, unknown = (new - old for old, new in zip(before, after))
        self.db.execute("""
            UPDATE volumes SET started=?,finished=?,unique_bytes=unique_bytes+?,
                               duplicate_files=duplicate_files+?,unknown_files=unknown_files+?
            WHERE root_id=?
        """, (started, now(), unique, duplicates, unknown, root_id))
        if self.db.execute("SELECT 1 FROM dirs WHERE root_id=? AND fsid IS NULL AND flags & 2 = 0 LIMIT 1",
                           (root_id,)).fetchone():
            raise usn.Unavailable("A directory could not be identified")
        self.db.execute("UPDATE checkpoints SET position=? WHERE root_id=?", (end, root_id))
        self.db.execute("DELETE FROM pending_changes WHERE root_id=?", (root_id,))
        self.db.executemany("INSERT INTO pending_changes VALUES (?,?,?,?,?)",
                            ((root_id, fsid, *change) for fsid, change in pending.items()))


def incremental_snapshot(previous, roots, stage, ignored, scopes, cancel):
    previous = Path(previous).resolve()
    uri = previous.as_uri() + "?mode=ro"
    result = Path(stage) / "report.sqlite"
    try:
        with closing(sqlite3.connect(uri, uri=True)) as source:
            if source.execute("PRAGMA user_version").fetchone() != (SNAPSHOT_VERSION,):
                return None
            cached = dict((name, (ident, fsid, serial, journal, position, scope)) for
                          name, ident, fsid, serial, journal, position, scope in source.execute("""
                SELECT d.name,d.id,d.fsid,v.serial,c.journal,c.position,c.scope FROM dirs d
                JOIN volumes v ON v.root_id=d.id LEFT JOIN checkpoints c ON c.root_id=d.id
                WHERE d.parent IS NULL
            """))
            if not any(root in cached and cached[root][3] and cached[root][5] == scopes[root] for root in roots):
                return None
        emit("STAGE", "正在读取上次快照和磁盘变更日志")
        with previous.open("rb") as reader, result.open("wb") as writer:
            while block := reader.read(4 * 1024 * 1024):
                cancel.check()
                writer.write(block)
        with closing(sqlite3.connect(result, uri=True)) as db:
            db.set_progress_handler(cancel.interrupt_sql, 25000)
            db.execute("PRAGMA synchronous=OFF")
            db.execute("ATTACH DATABASE ? AS baseline", (uri,))
            db.execute("CREATE TEMP TABLE changed_files(fsid BLOB,entry_id INTEGER,PRIMARY KEY(fsid,entry_id)) WITHOUT ROWID")
            for event, row in (("INSERT", "new"), ("DELETE", "old")):
                db.execute(f"""
                    CREATE TEMP TRIGGER track_{row} AFTER {event} ON main.entries BEGIN
                        INSERT OR IGNORE INTO changed_files VALUES (
                            coalesce({row}.fsid,x''), CASE WHEN {row}.fsid IS NULL THEN {row}.id ELSE 0 END
                        );
                    END
                """)
            next_id = itertools.count(db.execute("SELECT coalesce(max(id),0)+1 FROM dirs").fetchone()[0])
            full = []
            db.execute("BEGIN")
            for root in roots:
                cancel.check(force=True)
                old = cached.get(root)
                if old and old[3]:
                    ident, fsid, serial, journal, position, scope = old
                    db.execute("SAVEPOINT refresh_volume")
                    db.execute("DELETE FROM changed_files")
                    try:
                        VolumeRefresh(db, root, ident, fsid, serial, scopes[root], ignored, next_id, cancel).run(
                            (journal, position, scope))
                    except (OSError, usn.Unavailable):
                        db.execute("ROLLBACK TO refresh_volume")
                    else:
                        db.execute("RELEASE refresh_volume")
                        continue
                    db.execute("RELEASE refresh_volume")
                emit("STAGE", f"正在重新全量扫描 {root}（缺少可用的增量基线）")
                full.append(root)
            if len(full) == len(roots):
                return None
            db.execute("DROP TRIGGER track_new")
            db.execute("DROP TRIGGER track_old")
            for root, (ident, *_) in cached.items():
                if root not in roots or root in full:
                    for table in ("entries", "dirs", "volumes", "checkpoints", "pending_changes"):
                        db.execute(f"DELETE FROM {table} WHERE root_id=?", (ident,))
            db.commit()
            db.execute("DETACH DATABASE baseline")
            if full:
                merge_shards(db, scan_roots(full, stage, ignored, scopes, cancel, next(next_id)))
    except sqlite3.DatabaseError:
        cancel.check(force=True)
        return None
    return result


def validate_full_snapshot(db):
    expected = db.execute("SELECT sum(files),sum(bytes),sum(logical_bytes) FROM dirs WHERE parent IS NULL").fetchone()
    actual = db.execute("SELECT count(*),coalesce(sum(bytes),0),coalesce(sum(logical_bytes),0) FROM entries").fetchone()
    if expected != actual or db.execute("PRAGMA quick_check").fetchone() != ("ok",):
        raise RuntimeError("Snapshot consistency check failed.")


def full_snapshot(roots, stage, ignored, scopes, cancel):
    shards = scan_roots(roots, stage, ignored, scopes, cancel)
    cancel.check(force=True)
    # Use the largest shard in place. SQLite allocates fresh entry IDs for the others.
    result = Path(max(shards, key=os.path.getsize))
    db = connect(result)
    try:
        db.set_progress_handler(cancel.interrupt_sql, 25000)
        if len(shards) > 1:
            emit("STAGE", "正在合并各磁盘的扫描结果")
        merge_shards(db, (shard for shard in shards if Path(shard) != result))
        emit("STAGE", "正在建立文件树和增量索引")
        db.executescript(f"""
            CREATE INDEX dirs_parent_bytes ON dirs(parent, bytes DESC, name);
            CREATE INDEX entries_parent_bytes ON entries(parent, bytes DESC, name);
            CREATE INDEX dirs_root_fsid ON dirs(root_id, fsid);
            CREATE INDEX entries_root_fsid ON entries(root_id, fsid);
            PRAGMA user_version={SNAPSHOT_VERSION};
        """)
        emit("STAGE", "正在校验目录汇总和数据库完整性")
        validate_full_snapshot(db)
    finally:
        db.close()
    return result


def run(output, exclude=None, root=None, cancel_file=None, previous=None):
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
        stable = {native(p).casefold() for p in (output, exclude, cancel_file) if p}
        if exclude:
            excluded = native(exclude).casefold().rstrip("\\") + "\\"
            stable = {p for p in stable if not p.startswith(excluded)}
        elevated = bool(ct.windll.shell32.IsUserAnAdmin())
        scopes = {root: scan_scope(root, stable, elevated) for root in roots}
        ignored = stable | {native(stage).casefold()}
        try:
            result = incremental_snapshot(previous, roots, stage, ignored, scopes, cancel) if previous else None
            if result is None:
                emit("STAGE", "正在进行全量扫描")
                result = full_snapshot(roots, stage, ignored, scopes, cancel)
        except sqlite3.OperationalError:
            cancel.check(force=True)
            raise
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
    parser.add_argument("--previous", type=Path, help="Published snapshot to use for incremental refresh")
    args = parser.parse_args()
    try:
        run(args.output, args.exclude, args.root, args.cancel_file, args.previous)
    except (Cancelled, KeyboardInterrupt):
        emit("CANCELLED")
        sys.exit(2)
    except Exception as error:
        emit("ERROR", str(error))
        sys.exit(1)
