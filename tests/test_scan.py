"""Windows regression checks for snapshot identities, junctions and publication."""
from contextlib import closing
import os
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

SRC = Path(__file__).resolve().parents[1] / "src"
if sys.platform == "win32":
    import _winapi
    sys.path.insert(0, str(SRC))
    import scan


@unittest.skipUnless(sys.platform == "win32", "Windows filesystem APIs")
class SnapshotTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name) / "测试 tree"
        self.root.mkdir()
        self.output = Path(temp.name) / "report.sqlite"

    def command(self, *extra):
        return subprocess.run([sys.executable, "-B", SRC / "scan.py", "--root", self.root,
                               "--output", self.output, *extra], capture_output=True,
                              encoding="utf-8", timeout=20)

    def test_handles_match_and_links_do_not_expand(self):
        folder = self.root / "nested"
        folder.mkdir()
        item = folder / "中文.txt"
        item.write_bytes(b"x" * 12000)
        os.link(item, self.root / "second-name")
        link = folder / "loop"
        _winapi.CreateJunction(str(self.root), str(link))
        self.addCleanup(os.rmdir, link)
        hidden = self.root / "application-data"
        hidden.mkdir()
        (hidden / "ignored").touch()
        result = self.command("--exclude", hidden)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn(f"SCAN\t{self.root}\t3\t2", result.stdout)
        with closing(sqlite3.connect(self.output)) as db:
            serial, duplicates = db.execute("SELECT serial,duplicate_files FROM volumes").fetchone()
            self.assertEqual(duplicates, 1)
            self.assertEqual(db.execute("SELECT files,flags FROM dirs WHERE parent IS NULL").fetchone(), (2, 8))
            self.assertEqual(db.execute("SELECT flags FROM dirs WHERE name='loop'").fetchone(), (2,))
            for table, name, path in [("dirs", str(self.root), self.root), ("dirs", folder.name, folder),
                                      ("dirs", link.name, link), ("entries", item.name, item)]:
                with self.subTest(path=path), scan.handle(scan.native(path), 0x80) as handle:
                    expected_serial, identity = scan.identity(handle)
                    self.assertEqual(serial, expected_serial)
                    self.assertEqual(len(identity), 16)
                    self.assertEqual(db.execute(f"SELECT fsid FROM {table} WHERE name=?", (name,)).fetchone(),
                                     (identity,))

    def test_unsupported_extended_directory_class_uses_open_handle(self):
        (self.root / "child").write_bytes(b"x" * 8192)
        get_info = scan.get_info

        def use_full_class(handle, cls, buffer, size):
            if cls == 19:
                scan.ct.set_last_error(50)
                return 0
            return get_info(handle, cls, buffer, size)

        with scan.handle(scan.native(self.root), 1) as directory, patch.object(scan, "get_info", use_full_class):
            rows = list(scan.children(directory, scan.ct.create_string_buffer(scan.BUFFER_SIZE)))
        self.assertEqual(len(rows), 1)
        name, logical, allocated, _, _, file_id = rows[0]
        self.assertEqual((name, logical, allocated, file_id), ("child", 8192, 8192, None))

    def test_cancel_preserves_previous_snapshot(self):
        self.output.write_bytes(b"previous")
        cancel = self.root.parent / "request-cancel"
        cancel.touch()
        result = self.command("--cancel-file", cancel)
        self.assertEqual((result.returncode, result.stdout.strip()), (2, "CANCELLED"))
        self.assertEqual(self.output.read_bytes(), b"previous")
        self.assertFalse(list(self.output.parent.glob(".scan-*")))

    def test_parallel_subtrees_match_sequential_rows(self):
        from benchmark_scan import snapshot
        branches = self.root / "outer" / "inner"
        branches.mkdir(parents=True)
        data = self.root / "common.bin"
        data.write_bytes(b"x" * 12000)
        for i in range(64):
            leaf = branches / str(i) / "nested"
            leaf.mkdir(parents=True)
            os.link(data, leaf / "copy.bin")
        link = branches / "31" / "junction"
        _winapi.CreateJunction(str(self.root), str(link))
        self.addCleanup(os.rmdir, link)
        exclude = branches / "0" / "nested"
        sequential = self.output.with_name("sequential.sqlite")
        with patch.object(scan, "supports_parallel_reads", return_value=False), patch.object(scan, "emit"):
            scan.run(sequential, exclude, self.root)
        with patch.object(scan, "supports_parallel_reads", return_value=True), \
                patch.object(scan.os, "cpu_count", return_value=16), patch.object(scan, "emit"):
            scan.run(self.output, exclude, self.root)
        self.assertEqual(snapshot(self.output), snapshot(sequential))
        with closing(sqlite3.connect(self.output)) as db:
            self.assertEqual(db.execute("SELECT duplicate_files FROM volumes").fetchone(), (63,))
            self.assertEqual(db.execute("SELECT files,flags FROM dirs WHERE parent IS NULL").fetchone(), (64, 8))

    def test_largest_shard_preserves_both_roots(self):
        other = self.root.parent / "larger"
        other.mkdir()
        (self.root / "only").touch()
        for i in range(160):
            (other / (str(i) + "-entry")).touch()
        with patch.object(scan, "mounted_roots", return_value=[str(self.root), str(other)]), patch.object(scan, "emit"):
            scan.run(self.output)
        with closing(sqlite3.connect(self.output)) as db:
            rows = db.execute("SELECT name,files FROM dirs WHERE parent IS NULL")
            self.assertEqual(dict(rows), {str(self.root): 1, str(other): 160})
            self.assertEqual(db.execute("SELECT count(*) FROM entries").fetchone(), (161,))
            self.assertEqual(db.execute("SELECT count(*) FROM volumes").fetchone(), (2,))

    @unittest.skipUnless(os.environ.get("WDM_SCANNER_EXE"), "set WDM_SCANNER_EXE to a frozen scanner")
    def test_frozen_workers_start_and_cancel(self):
        exe = os.environ["WDM_SCANNER_EXE"]
        self.output.write_bytes(b"previous")
        cancel = self.root.parent / "request-cancel"
        events = self.root.parent / "progress.txt"
        expected = min(2, len(scan.mounted_roots()))
        with events.open("wb") as stream:
            process = subprocess.Popen([exe, "--output", self.output, "--cancel-file", cancel],
                                       stdout=stream, stderr=subprocess.STDOUT)
        drives = set()
        try:
            deadline = time.monotonic() + 20
            while process.poll() is None and time.monotonic() < deadline:
                lines = events.read_bytes().decode("utf-8", "replace").splitlines()
                drives = {line.split("\t")[1] for line in lines if line.startswith("SCAN\t")}
                if len(drives) >= expected:
                    break
                time.sleep(0.05)
        finally:
            cancel.touch()
            process.wait(timeout=20)
        output = events.read_text(encoding="utf-8")
        self.assertGreaterEqual(len(drives), expected, output)
        self.assertEqual(process.returncode, 2, output)
        self.assertIn("CANCELLED", output)
        self.assertEqual(self.output.read_bytes(), b"previous")
        self.assertFalse(list(self.output.parent.glob(".scan-*")))


if __name__ == "__main__":
    unittest.main()
