#!/usr/bin/env python3
"""Parser-only tests; synthetic lines are not kernel I/O evidence."""
import importlib.util, pathlib, tempfile, unittest, sys
sys.dont_write_bytecode=True
spec=importlib.util.spec_from_file_location('validation',pathlib.Path(__file__).with_name('Validate-BoundedOnlineIo.py'))
validation=importlib.util.module_from_spec(spec); spec.loader.exec_module(validation)
class TraceParserTests(unittest.TestCase):
    def parse(self, lines):
        with tempfile.TemporaryDirectory() as temporary:
            trace=pathlib.Path(temporary)/'trace';trace.write_text(lines)
            return validation.trace_summary(trace,pathlib.Path('/tmp/dataset'))
    def test_root_directory_is_dataset_and_sidecar_is_not(self):
        result=self.parse('''1 openat(AT_FDCWD, "/tmp/dataset", O_RDONLY) = 3</tmp/dataset>
1 getdents64(0x3, 0x1234, 0x8000) = 0x48
1 close(3</tmp/dataset>) = 0
1 openat(AT_FDCWD, "/tmp/dataset.fixture.json", O_RDONLY) = 3</tmp/dataset.fixture.json>
1 read(0x3, 0x1234, 0x1000) = 0x10
1 close(3</tmp/dataset.fixture.json>) = 0
''')
        self.assertEqual(1,result['datasetGetdentsSyscalls']);self.assertEqual(0,result['datasetReadSyscalls'])
        self.assertEqual(1,result['datasetOpenSyscalls']);self.assertEqual(1,result['datasetCloseSyscalls'])
    def test_reassembly_failures_and_interruptions_are_explicit(self):
        result=self.parse('''1 openat(AT_FDCWD, "/tmp/dataset/a", O_RDONLY) = 3</tmp/dataset/a>
1 pread64(0x3, 0x1234, 0x20, 0x0 <unfinished ...>
2 read(0x3, 0x2345, 0x10) = -1 EIO (Input/output error)
1 <... pread64 resumed>) = 0x20
2 read(0x3, 0x2345, 0x10) = ? ERESTARTSYS (To be restarted)
1 close(3</tmp/dataset/a>) = 0
''')
        self.assertEqual(3,result['datasetReadSyscalls']);self.assertEqual(64,result['datasetRequestedBytes'])
        self.assertEqual(32,result['datasetReturnedBytes']);self.assertEqual(1,result['datasetFailedSyscalls'])
        self.assertEqual(1,result['datasetInterruptedSyscalls'])
    def test_inflight_dataset_read_keeps_start_owner_after_fd_reuse(self):
        result=self.parse('''1 openat(AT_FDCWD, "/tmp/dataset/a", O_RDONLY) = 3</tmp/dataset/a>
1 pread64(0x3, 0x1234, 0x20, 0x0 <unfinished ...>
2 close(3</tmp/dataset/a>) = 0
2 openat(AT_FDCWD, "/tmp/external", O_RDONLY) = 3</tmp/external>
1 <... pread64 resumed>) = 0x20
2 read(0x3, 0x4321, 0x10) = 0x10
''')
        self.assertEqual(1,result['datasetReadSyscalls']);self.assertEqual(32,result['datasetReturnedBytes'])
    def test_stat_paths_use_dataset_boundary(self):
        result=self.parse('''1 statx(AT_FDCWD, "/tmp/dataset/a", 0, 0, {}) = 0
1 newfstatat(AT_FDCWD, "/tmp/dataset.fixture.json", {}, 0) = 0
1 statx(AT_FDCWD, "/tmp/dataset-neighbor/a", 0, 0, {}) = 0
1 newfstatat(3</tmp/dataset>, "", {}, AT_EMPTY_PATH) = 0
''')
        self.assertEqual(2,result['datasetStatSyscalls'])
    def test_unclassified_or_incomplete_dataset_reads_fail(self):
        for line in ['read(0x3, unknown, 0x20) = 0x20','read(0x3, 0x1234, 0x20 <unfinished ...>']:
            with self.assertRaises(RuntimeError):
                self.parse('1 openat(AT_FDCWD, "/tmp/dataset/a", O_RDONLY) = 3</tmp/dataset/a>\n1 '+line+'\n')
if __name__=='__main__': unittest.main()
