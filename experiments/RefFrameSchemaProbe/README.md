# ref 帧格式与可变初始化边界探针

本探针只使用 BCL 和主线 public Rbf/Data：实验性数值身份/header codec、首两帧 shape/full CRC，以及普通 RBF 尾恢复。没有真正 RefId、FrameAddress、RootMap decoder、VersionStore、格式门或目录发布；15B RootMap bytes 是先前独立 synthetic 地址 golden，首两帧 helper 不替代 RootMap 解码和自有结果资格。

```powershell
dotnet build experiments/RefFrameSchemaProbe/Probe.csproj -c Release
dotnet run --project experiments/RefFrameSchemaProbe/Probe.csproj -c Release --no-build -- W:/
```

2026-10-09 在 Windows、SDK 10.0.201 / runtime 10.0.5、W: 上执行；最终 Release build 无警告/错误，75 项断言通过。[原始 JSON](evidence/2026-10-09-result.json)记录字段 golden、尺寸和实际恢复 report。项目不注册 solution/pack；每次在指定已有目录下创建唯一子目录，只删除自己创建的确切文件和已空子目录，不扫描或递归删除。未测性能、进程终止、rename、Linux 或包消费。

| 检查 | 结果与界限 |
| --- | --- |
| 无来源/有来源 header | decoded payload 恰28/44B，唯一格式版本、VSID16、自身RefId8，有来源再加SourceRefId8与Packed LE8；独立golden，不依赖CLR内存或SizedPtr.Serialize |
| 坏字段 | 拒绝其他长度、未知版、错身份、零/自链接source与不可能ticket；最大起点的合法ticket末端可越界。只建立数值资格，不证明来源存在/真实主链 |
| RBF物理尺寸与cold只读 | header frame56/72B，初始起点64/80，空map初始化文件100/116B；首两帧完整CRC通过，header-only和第二帧未知kind被实验helper拒绝 |
| 最小长度前检的反例 | header28 + 初始15B body 完整文件112B；删末8B Key/Fence后为104B，仍≥空map最小初始化100B。只读工厂拒绝且不改bytes；可写CompletedTail在offset64补回112B，首两帧形状与完整CRC通过；再次打开Action=None |
| 已完成初始化后的更新残尾 | header28 + 初始empty完整100B，再追加15B更新；删末8B后140B，CompletedTail在offset100恢复到148B，原初始化保持完整 |

因此“空map的最小文件长度＋恢复后检查AffectedFrameOffset”不能可靠防止可变初始帧被认领：首次事后拒绝不会撤销底层补尾，第二次已无旧报告。当前public只读工厂要求整个尾部闭合，不能在合法更新残尾旁先取得只读首两帧资格；offline recovery scanner也不支持RBF3。该结果是需要继续设计恢复前初始化保护的实证，不是所需公共前缀能力已经存在或任何上层恢复方案已获资格。

正式目录应按协议只包含flush/close后的完整初始化；上述人为截短文件不是合法目录发布中断的证据。它专门检验设计中拒绝正式缺坏初始化的边界，不能被解释为正常rename会产生半初始化，亦不扩大ProcessCrashOnly故障模型。
