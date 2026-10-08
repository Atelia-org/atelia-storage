# Tagged Value 意向记录

日期：2026-10-08。状态：**Intent / Deferred，需求尚未清楚；不进入施工、不分配 tag 值、不规定自有 wire format**。
本文件记录用户明确要求的候选方向。与 [Bare Primitive Value 草案](bare-primitive-value.md) 分开：Bare 面向已知 schema 的基础编解码；Tagged 不构成 Bare、FrameStore 或 VersionStore 的实施前置。

## 为什么曾考虑它

在值类型事先未知的 mixed collection、诊断工具或跨组件数据交换中，类型标识与容器边界可以让 reader 不依赖外部 schema 解释内容。StateJournal 的 mixed ValueBox 已有 tagged scalar 需求，但其 DurableRef、SymbolId、Revision 等领域规则不构成通用 Tagged Value 的需求证明；普通 FS/VS header 和 RootMap 已知 schema，也没有要求它们改用 schemaless 格式。
用户本轮指出：需求本身尚不清楚，没有充足理由独立于 CBOR 等成熟标准另起炉灶。此前讨论的小整数内联、LE、自适应字符串、array/map、SkipValue 等均保留为评估问题，不升级为本轮工程合同。

## 优先评估成熟标准

首先对具体 consumer 评估 CBOR 或其他成熟二进制格式。CBOR 已提供标量、bytes/text、array/map 和扩展 tag；通用 reader 与特定协议的 deterministic/profile 要求可以分别规定。[RFC 8949](https://www.rfc-editor.org/rfc/rfc8949.html)
CBOR 多字节数值采用 BE、标准 text string 使用 UTF-8。BE 本身不增加字节数；中文较多时 UTF-16LE 的 payload 可能较短，但这些局部差异尚不足以证明值得维护新协议、实现与工具生态。需用真实消息比较完整尺寸、读写 CPU、allocation 和接入成本，而非只比较一个字符或一条数值指令。
未来 Tagged 不必复用 Bare 的完整 wire；采用标准 CBOR 可以是与 Bare 并存的选择，二者是否共享少量内部工具由实现决定。不得为了 Bare 的统一目标提前排除标准。

## 重新启动设计的条件

| 待澄清问题 | 需要的证据 |
| --- | --- |
| 谁需要自描述值 | 一个实际 consumer 的输入、输出、操作频率和 schema 未知原因 |
| 要保留哪些语义 | 数学值或 CLR 类型/原始 bits、null、UTF-16 code units、map key 与重复规则、领域引用 |
| 是 tagged scalar 还是完整文档 | 是否真需要嵌套 array/map、流式 token、跳过值、unknown 扩展；不能以 scalar dispatcher 宣称已支持全部 |
| 成熟标准有何不足 | 受约束的实测/接入失败；以及标准 profile、bytes/tag 等方案为何仍不满足 |
| 是否值得自有协议 | 所需差异收益足以承担规范、实现、golden、fuzz/边界、版本与诊断工具的维护成本 |

未达到这些条件时维持意向记录。即使未来需要 Tagged，也先给 consumer 定义最小可用范围；不自动要求 DOM、reflection、全局 tag registry 或完整 object serializer。

## 当前处置

本轮只保留意向与接纳条件；不创建 Tagged 项目/API、保留数值区间或预埋 Bare type tags。Bare 草案可以独立审定施工，FS/VS 的 schema records 继续由各自层定义。何时重开讨论由实际下游需求推动。
