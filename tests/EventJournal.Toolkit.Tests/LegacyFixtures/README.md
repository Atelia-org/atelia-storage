# 固定旧 binary fixtures

来源为完整 `bb7c4fb3eb6477783c70ee61bc62b832be195d07` 导出源码，SDK `10.0.201`。
`eng/Generate-LegacyFixtures.py` 在独立进程编译 `eng/LegacyFixtureGenerator.cs.txt`，只调用旧 public API；旧生产源码不修改，旧、新同名 assembly 不进入同一进程。

```bash
python3 -B eng/Generate-LegacyFixtures.py --work /tmp/legacy-export-UNIQUE --output /tmp/legacy-fixtures-UNIQUE
```

固定数据共四组：empty、events-only、complex、empty-active。complex 包含实际 CAS orphan、tag 指向 orphan、与 branch 同名的 tag、unborn branch、归档后同名新 RefId、fork 来源后来归档、三段 event、Identity/Brotli/Zlib 和非零 Hint。旧 writer 的实际 Close/Archive 时间为 `1790644653237` / `1790644653289`，差 `52ms`；时间观察未经过字节重写。empty-active 的 event/ref 最高 segment2 为合法 4B HeaderFence，由旧 public `OpenActiveWriter` 无 append 生成。

每组 `expected.json` 保存旧只读 binary 的语义观察；`provenance.json` 保存生成器 hash、源码 archive hash、SDK、固定文件长度/hash及 `fixtureDirectories`。生成器重复运行的 wall clock 与地址以外的时间观察会变化，固定文件 hash 用于确认本次实证，不声称生成物字节可复现。

Git 不保存空目录。**物化 fixture 时先按 `fixtureDirectories[fixture]` 创建目录，再复制文件。** 清单路径相对于组目录，`journal/refs/objects` 等空 roots 必须恢复；不能在 journal 内添加 `.gitkeep` 或任何 metadata sidecar。测试期望与来源清单位于 journal 外，不属于 upgrade 输入。
