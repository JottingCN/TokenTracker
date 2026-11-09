# 费用与定价

上级：[AGENTS.md](../../AGENTS.md) · 相关：[OTel 解析](otel-parsing.md)、[存储](storage.md)

## 1. 价格来源

- **不内置任何模型价格**，必须按模型配置；未配置显示「未定价」，保存价格后必须重算历史 span（`BackfillCostBreakdown`）。未定价模型不计算峰谷。
- 模型设置（`/api/models` + `model_profiles` 表）保存供应商、图标与价格。**模型 ID 是唯一键（`COLLATE NOCASE`）**，同时是价格归一化键来源；供应商与图标只影响展示，价格存 `model_prices`，两者按「模型列表合并、价格精确匹配优先」投影到同一行，不要为图标另建模型名体系。
- 图标默认按供应商推导（内置颜色 + 首字母）；自定义时为单字符 + 颜色，或上传图片（前端 canvas 裁剪为 64×64 PNG 后以 data URL 存库，服务端只接受 png/jpeg/webp 并限制长度）。

## 2. 单价结构

模型设置「模型」页只做摘要：四列基准单价（`uncachedInput` / `cachedInput` / `cacheWrite` / `output`，不含峰谷与长上下文分档）放在「价格（每百万 tokens）」列组下；峰谷与长上下文档在「价格」页维护。**列名与取值必须成对**，不要把 `cachedInput` 放在“输入”标题下。

- 支持按单次调用输入 token 阈值（含缓存命中）设长上下文第二档：超过阈值**整次调用**按长上下文档计价；峰谷与分档可同时启用。
- Anthropic/Claude 的 `input_tokens` 不含缓存：解析时把 cache read + cache creation 合并为总输入；cache creation 按缓存写入价，未配置时回退未缓存输入价。**实测 `cache_creation` 两条管线都是 0 次，该分支目前不会触发，费用回归不要依赖它。**

## 3. 峰谷与节假日

- 峰谷按调用**结束时间**判定（无结束时间回退开始时间）：跨入峰段的调用整体按峰价，与官方按请求完成时间计费对齐。
- 北京工作日 9:00–12:00、14:00–18:00 为峰段；周末与中国法定节假日为空闲。
- 节假日优先读 SQLite，缺失时按年拉取（`TOKENTRACKER_HOLIDAY_API_URL`）并缓存；数据到达后 `HolidayService` 自行重算该年费用。
- 节假日拉取最长 8 秒，**必须在端口就绪后后台执行**，不能阻塞启动（见[服务生命周期](lifecycle.md)）。

## 4. 展示

- 费用展示统一 6 位小数。
- Web 支持人民币 / 美元 / 积分（`localStorage`），价格输入按当前单位换算后**仍以 CNY 保存**。
- 四类费用（`cacheCostCny` / `uncachedCostCny` / `outputCostCny` / `reasoningCostCny`）与 `costCny` 的关系：`costCny = 四者之和`；`ModelRow` / `SummaryDto` / `SessionTurnRow` 必须同时带齐，否则卡片与表格对不上。