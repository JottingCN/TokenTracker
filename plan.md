# Token 命中率与费用统计 VS Code 插件 · 开发文档

**版本**：v1.0（设计定稿）
**适用读者**：扩展开发者
**依赖基线**：`@github/copilot-sdk`（锁定精确版本）、VS Code ≥ 1.90


## 1. 文档定位与范围

本文档描述一个 VS Code 扩展的完整开发方案，目标是从 Copilot 会话的 `assistant.usage` 事件流中，**实时统计 Token 缓存命中率，并按 DeepSeek 公开价目表估算每次调用的费用**，通过状态栏和 Webview 面板向用户呈现。

**明确不做**：

- 不替代 VS Code 内置的 Copilot 用量视图或 OpenTelemetry 导出；
- 不尝试从 Token 计数反推“缓存为什么失效”（失效原因在数据上不可归因）；
- 第一版不接入 `session.usage.getMetrics`（该 API 标记为实验性，官方建议“Pin both the SDK and the Copilot CLI runtime”）。


## 2. 技术架构总览

```
┌─────────────────────────────────────────────────────┐
│ Copilot 会话（CopilotSession）                        │
│   session.on("assistant.usage", handler)             │
└──────────────────────┬──────────────────────────────┘
                       │ AssistantUsageEventData
                       ▼
┌─────────────────────────────────────────────────────┐
│ 采集层 collector.ts                                   │
│   · 字段存在性检查（cacheReadTokens / inputTokens）     │
│   · initiator / parentToolCallId 过滤                 │
│   · 生成 TurnSample                                   │
└──────────────────────┬──────────────────────────────┘
                       │ TurnSample
                       ▼
┌─────────────────────────────────────────────────────┐
│ 计算层（纯函数，无副作用）                              │
│   metrics.ts   → hitRate / cacheDelta                 │
│   pricing.ts   → costCNY / pricingPeriod              │
│   calendar.ts  → isPeak(date)                         │
└──────────────────────┬──────────────────────────────┘
                       │ 聚合结果
                       ▼
┌──────────────────────────┬──────────────────────────┐
│ 展示层                    │ 持久化层                  │
│ statusBar.ts（实时）      │ globalState（会话快照）    │
│ panel.ts（Webview 下钻）  │ Webview setState（UI 态） │
└──────────────────────────┴──────────────────────────┘
```

核心原则：**采集与计算分离，计算与展示分离**。即使价目表、SDK 字段或 UI 形态发生变化，也只影响单一模块。


## 3. 数据源：`assistant.usage` 事件

### 3.1 事件语义

`assistant.usage` 在**每一轮中的每一次模型 API 调用**完成后触发，包括子代理发起的调用。它是**瞬态事件**，仅在调用发生时实时推送，会话恢复时**不会重放**。这意味着扩展必须在事件触发时立即落盘需要长期保留的数据。

### 3.2 与本文档直接相关的字段

`AssistantUsageEventData` 的完整字段列表包括 `model`、`inputTokens`、`outputTokens`、`cacheReadTokens`、`cacheWriteTokens`、`reasoningTokens`、`cost`、`initiator`、`parentToolCallId` 等。本文档使用的字段如下：

| 字段 | 类型 | 用途 |
|:---|:---|:---|
| `model` | `string` | 选择价目表条目；按模型隔离缓存基线 |
| `inputTokens` | `number \| null` | 命中率分母；费用计算的输入总量 |
| `cacheReadTokens` | `number \| null` | 命中率的分子；缓存命中输入 Token |
| `outputTokens` | `number \| null` | 输出费用计算 |
| `initiator` | `string` | 噪声过滤：排除 `sub-agent`、`mcp-sampling` 等 |
| `parentToolCallId` | `string \| null` | 噪声过滤：非空表示子代理嵌套调用 |

**`cost` 字段不可用于本插件的费用统计**。它表示 Copilot 自身的 Premium request multiplier，不是人民币法币金额，也不反映 DeepSeek 的峰谷折扣或缓存折扣。费用必须由扩展基于 Token 数和价目表自行计算。

**`parentToolCallId` 在部分 SDK 版本中已标记为 deprecated**，建议改用 envelope 层的 `agentId` 做子代理归属。实现时应对二者做兼容读取：优先 `agentId`，回退 `parentToolCallId`。


## 4. 采集层：过滤与 `TurnSample` 生成

### 4.1 过滤规则

并非所有 `assistant.usage` 事件都应进入命中率和费用统计。以下三类事件必须排除：

| 排除条件 | 判断依据 | 理由 |
|:---|:---|:---|
| 子代理调用 | `initiator === "sub-agent"` 或 `agentId/parentToolCallId` 非空 | 子代理有独立缓存上下文，混算会扭曲主会话命中率；其 Token 已在主会话的输入中被计入，再次计费会重复 |
| MCP sampling 调用 | `initiator === "mcp-sampling"` | 与主会话缓存无关 |
| 关键字段缺失 | `cacheReadTokens` 或 `inputTokens` 为 `null`/`undefined`/`NaN` | 不能把 missing 当 0 处理，否则命中率被低估为 0，费用被高估（全部输入按未命中价） |

过滤后的样本称为 `TurnSample`：

```typescript
interface TurnSample {
  timestamp: number;           // epoch ms
  model: string;
  inputTokens: number;
  cacheReadTokens: number;
  outputTokens: number;
  hitRate: number;             // cacheReadTokens / inputTokens
  cacheDelta: number | null;   // 同模型相邻调用的 cacheReadTokens 变化
  isCacheDrop: boolean;
  costCNY: number | null;      // null = 无法估算（未知模型或字段缺失）
  pricingPeriod: "peak" | "idle";
}
```

### 4.2 订阅骨架

```typescript
import { joinSession } from "@github/copilot-sdk/extension";

const session = await joinSession({ /* ... */ });

session.on("assistant.usage", (event) => {
  const d = event.data;
  if (!d) return;

  if (d.initiator === "sub-agent" || d.initiator === "mcp-sampling") return;
  if (d.parentToolCallId != null) return;          // 兼容旧版本
  // 若有 agentId 字段，同样在此过滤

  const input = d.inputTokens;
  const cached = d.cacheReadTokens;
  if (!Number.isFinite(input) || !Number.isFinite(cached) || input <= 0) return;

  // 进入计算层
});
```

`joinSession` 返回一个 `CopilotSession` 对象，支持 `session.on(...)` 订阅事件。如果 `joinSession` 在非 CLI 环境中 reject，扩展应在 `activate` 中捕获异常，状态栏显示“Copilot 会话不可用”，不阻塞扩展加载。


## 5. 计算层

### 5.1 命中率

单次调用的命中率定义为：

> **`hitRate = cacheReadTokens / inputTokens`**

其中 `inputTokens` 是本次调用的**总输入 Token**，**包含** `cacheReadTokens`。这一点必须与后端语义对齐：在 Copilot SDK 的 `assistant.usage` 事件中，`inputTokens` 是“the FULL prompt for this call”，而不是“未命中输入”。因此费用计算中的“未命中输入”必须显式用 `inputTokens - cacheReadTokens` 得到，而不能直接用 `inputTokens` 乘以未命中单价。

### 5.2 缓存下降检测（`cacheDelta` 与 `isCacheDrop`）

比值命中率对大输入敏感：一次大文件粘贴会把 `inputTokens` 拉高，即使 `cacheReadTokens` 不变，比值也会被稀释。因此**比值用于展示，绝对变化量用于告警**。

```typescript
// 按 model 维护上一次的 cacheReadTokens
const prevCacheByModel = new Map<string, number>();

function computeCacheDelta(model: string, cacheReadTokens: number): number | null {
  const prev = prevCacheByModel.get(model);
  prevCacheByModel.set(model, cacheReadTokens);
  if (prev === undefined) return null;
  return cacheReadTokens - prev;
}

// isCacheDrop = cacheDelta !== null && cacheDelta <= -4096
```

阈值 4096 是可配置的常量。选择绝对值而非百分比，是为了避免小会话中个位数 Token 波动触发噪声告警。

**缓存基线必须按 `model` 隔离**。切换模型时，`cacheReadTokens` 的绝对值不可跨模型比较，基线应重置。

### 5.3 费用计算

#### 时段判定

高峰时段为**北京时间周一至周五（不含中国法定节假日）9:00–12:00、14:00–18:00**；其余时段，包括周末和法定节假日全天，均为空闲时段。空闲时段单价是高峰时段的**一半**。

实现上用 `Intl.DateTimeFormat` 配合 `Asia/Shanghai` 获取北京时间的星期、小时、分钟，无需额外时区库：

```typescript
function getBeijingParts(date: Date) {
  const fmt = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Asia/Shanghai",
    weekday: "short", hour: "numeric", minute: "numeric", hour12: false,
  });
  // 从 formatToParts 中提取 weekday / hour / minute
}
```

节假日集合建议使用成熟的 npm 包，而非手工维护：`chinese-workday` 支持 2018–2026 年完整法定节假日与调休数据，数据来源为国务院公告；`chinese-days` 同样支持 2004–2026 年。插件应在 README 中提示：**节假日数据需要每年随国务院安排更新**，跨年后若包未更新，费用估算的时段判定可能出现偏差。

判定逻辑：

> **若北京时间是周六或周日，或该日期在法定节假日集合中 → 空闲；否则，再看小时是否落在 [9,12) 或 [14,18) 内。**

注意 DeepSeek 的规则明确“调休上班的周末仍按周末识别”，因此**调休补班的周六/周日仍归为空闲**，不应按“工作日”处理。

#### 单价表

以 DeepSeek 官方定价页为准：

| 项目（元 / 百万 Token） | `deepseek-flash` 空闲 | `deepseek-flash` 高峰 | `deepseek-v4-pro` 空闲 | `deepseek-v4-pro` 高峰 |
|:---|:---|:---|:---|:---|
| 缓存命中输入 | 0.02 | 0.04 | 0.15 | 0.30 |
| 缓存未命中输入 | 1 | 2 | 4.5 | 9.0 |
| 输出 | 4 | 8 | 13.5 | 27.0 |

旧模型名 `deepseek-v4-flash`、`deepseek-v4-flash-vision-exp` **仍按 Flash 价格计费**。因此 `PRICES` 表必须将这两个旧名映射到与 `deepseek-flash` 相同的价格档，否则 `assistant.usage` 返回旧名时费用计算会返回 `null`，造成统计缺口。

#### 单次费用公式

```typescript
function computeCostCNY(
  model: string,
  inputTokens: number,
  cacheReadTokens: number,
  outputTokens: number,
  isPeak: boolean
): number | null {
  const entry = PRICES[model];          // 含旧名映射
  if (!entry) return null;

  const p = isPeak ? entry.peak : entry.idle;
  const uncachedInput = Math.max(0, inputTokens - cacheReadTokens);

  return (
    (cacheReadTokens / 1_000_000) * p.cachedInput +
    (uncachedInput / 1_000_000) * p.uncachedInput +
    (outputTokens / 1_000_000) * p.output
  );
}
```

`Math.max(0, ...)` 是对异常数据的防御：理论上 `cacheReadTokens ≤ inputTokens`，但不应让异常上游数据产生负费用。


## 6. 展示层

### 6.1 状态栏

状态栏承担“一眼可读”的实时展示，格式：

```
$(pulse) Cache 87.3% | ~¥0.24 | 12.4k / 14.2k
```

- 第一个数是**本会话累计命中率**（累计 `cacheReadTokens / inputTokens`）；
- `~¥0.24` 是**本会话累计估算费用**，`~` 前缀提示“估算而非账单”；仅当至少一个样本的 `costCNY` 非 null 时展示；
- `12.4k / 14.2k` 是累计 `cacheReadTokens / inputTokens` 绝对值。

当 `isCacheDrop` 触发时，状态栏短暂切换为警示样式并显示 `$(warning) Cache drop -4.2k`，数秒后恢复为正常比率展示。

**更新频率必须节流**：状态栏不应在每个 `assistant.usage` 事件上刷新。建议在 `TurnSample` 生成后，以 **500ms–1s 的节流窗口**更新一次；在 `turn_end` 或样本确定后强制刷新一次。VS Code 扩展中常见做法是用 `setTimeout` 实现节流/防抖，避免高频 DOM/状态栏更新造成宿主开销。

状态栏用 `vscode.window.createStatusBarItem` 创建，设置 `command` 属性使其点击时打开 Webview 面板。

### 6.2 Webview 面板

面板承担“下钻”职责，至少包含四个区块：

1. **会话累计概览**：总命中率、按模型的命中率拆分、总 input / cacheRead / cacheWrite / output。
2. **费用概览**：累计估算费用（按模型拆分）；高峰时段费用 vs 空闲时段费用的占比；**缓存节省估算** = `cacheReadTokens × (未命中单价 − 命中单价) / 1e6`，直观展示缓存的经济价值。
3. **时间线**：按轮次列出 `hitRate`、`cacheReadTokens`、`costCNY`、`pricingPeriod`，用表格或简单柱状图呈现，标出 `isCacheDrop` 事件。
4. **口径说明**（固定文案）：明确“费用为基于 DeepSeek 公开价目表的估算，与 GitHub Copilot 订阅账单无关；命中率反映缓存复用比例，下降不等于故障”。

Webview 通过 `vscode.window.registerWebviewViewProvider` 注册为侧边栏视图。扩展 → Webview 用 `webview.postMessage({ type: "samples", data })`；Webview → 扩展用 `vscode.postMessage({ type: "export" })` 等命令消息。

**CSP 与资源加载**：前端资源通过 `asWebviewUri` 加载，禁用 `unsafe-inline`，按 VS Code 官方 Webview 安全要求配置 CSP。


## 7. 持久化策略

持久化分两层，对应不同生命周期：

| 层 | 机制 | 存储内容 | 生命周期 |
|:---|:---|:---|:---|
| **扩展侧** | `context.globalState` | 已结束会话的 `TurnSample` 聚合快照（按会话 ID + 时间戳） | 跨窗口、跨重启 |
| **Webview 侧** | `acquireVsCodeApi().setState()` / `getState()` | 滚动位置、当前筛选条件等 UI 状态 | 面板隐藏/恢复（Webview 销毁重建后恢复） |

VS Code 的三层状态持久化策略中，`setState()/getState()` 用于“**within-session**”的 Webview 状态恢复，而 `globalState` 才是跨会话持久化的正确载体；`retainContextWhenHidden` 应谨慎使用，除非状态确实无法序列化。本插件的 Webview 状态全部可序列化，因此**不启用** `retainContextWhenHidden`。

**会话恢复（resume）时的关键处理**：`assistant.usage` 不会重放历史事件。扩展需要在 `session` 恢复后，从 `globalState` 中读取该会话最后保存的 `prevCacheByModel` 基线，否则恢复后的第一轮 `cacheDelta` 为 `null`，`isCacheDrop` 检测失效。

**持久化写入时机**：每生成一个 `TurnSample` 后立即写入 `globalState`（或按短节流批量写入）。因为事件是瞬态的，不能依赖面板打开时再回读。


## 8. 关键风险与应对

### 8.1 字段映射风险：DeepSeek 直接调用时 `cache_read_tokens` 可能始终为 0

这是本插件最需要警惕的上游问题。DeepSeek 的 OpenAI 兼容 API 在 `usage` 中返回的是 **`prompt_cache_hit_tokens`** 和 **`prompt_cache_miss_tokens`**，而不是 OpenAI/OpenRouter 风格的 `prompt_tokens_details.cached_tokens`。已有 bug 报告指出，直接调用 `api.deepseek.com` 时，某些网关/中间层只读取 OpenAI 风格的 `cached_tokens`，导致 **`cache_read_tokens` 始终被计算为 0**，命中率统计“silently always 0%”。

**应对**：

- 插件应在 README 中明确声明这一风险，提示用户在“命中率持续为 0 但缓存实际可能生效”时检查上游字段映射；
- 在代码中做**运行时探测**：如果连续 N 次（如 5 次）`cacheReadTokens === 0` 且 `inputTokens` 显著大于 0，状态栏可显示“缓存字段可能未映射”的提示，而不是让用户误以为缓存完全失效；
- 如果条件允许，在采集层兼容读取 `prompt_cache_hit_tokens`（若事件载荷中存在该字段），作为 `cacheReadTokens` 的回退来源。

### 8.2 `parentToolCallId` 的版本兼容

该字段在部分 SDK 版本中已标记为 deprecated，建议用 `agentId` 替代做子代理归属。采集层应同时读取二者，任一非空即排除，避免因 SDK 版本差异导致子代理调用混入主会话统计。

### 8.3 `cost` 字段不可用于法币费用

Copilot SDK 的 `cost` 是 Premium request multiplier，不是人民币金额。**费用统计必须完全基于 Token 数和 DeepSeek 价目表自行计算**。在 UI 中必须将“估算费用”与“Copilot 账单”明确区分，避免用户混淆。

### 8.4 用户误读命中率

命中率下降可能是**上下文正常变更**（如用户编辑了系统提示、工具列表变化、会话重置）导致的缓存失效，而非“故障”。缓存中断的根因调查显示，首轮 `cache_read_tokens` 的“残值”可以区分失效位置（system prompt 重建、tools 变化、mid-conversation 分歧等），但**插件不应尝试自动归因**，只需在面板中说明“下降不等于故障”。

### 8.5 节假日数据时效性

`chinese-workday` / `chinese-days` 等包虽然覆盖到 2026 年，但**国务院年度安排发布后需要更新依赖**。扩展应在 README 中标注当前内置的节假日数据截止年份，并在跨年后首次激活时通过 `globalState` 记录一个“请检查节假日数据更新”的提示标记。


## 9. 分阶段开发计划

| 阶段 | 内容 | 产出 | 预估 |
|:---|:---|:---|:---|
| **阶段 1：探针** | `joinSession` + 打印 `assistant.usage` 原始字段；用真实会话确认 `initiator`、`parentToolCallId`/`agentId`、`cacheReadTokens` 的实际取值 | 一份字段观察记录，决定过滤规则是否需要调整 | 1–2 天 |
| **阶段 2：采集与计算** | 实现过滤、`TurnSample` 生成、`hitRate`、`cacheDelta`、`computeCostCNY`、`isPeak`；为纯函数编写单元测试 | `collector.ts` / `metrics.ts` / `pricing.ts` / `calendar.ts` + 测试 | 2–3 天 |
| **阶段 3：状态栏** | 节流更新、缓存下降警示、点击打开面板 | `statusBar.ts` | 1 天 |
| **阶段 4：Webview 与持久化** | 面板四区块、`globalState` 会话快照、Webview `setState` UI 态、resume 基线恢复 | `panel.ts` + 持久化接入 | 2–3 天 |
| **阶段 5：边界与发布** | 字段映射探测、版本兼容、CSP、README 口径声明、节假日数据时效提示 | 可发布的 VSIX | 2 天 |

**建议的启动顺序**：先完成阶段 1 的探针代码。`initiator` 的实际取值分布、`agentId` 是否已取代 `parentToolCallId`、`cacheReadTokens` 在目标环境中是否非零，这三个观察结果会直接决定阶段 2 的过滤规则和风险提示策略是否需要调整。


## 10. 测试要点

纯函数层应达到较高覆盖率，重点覆盖以下场景：

- **过滤**：`initiator === "sub-agent"` 被排除；`initiator === "mcp-sampling"` 被排除；`parentToolCallId` 非空被排除；`agentId` 非空被排除；字段缺失被跳过（而非按 0 处理）。
- **命中率**：`inputTokens = 0` 返回 `null`；`cacheReadTokens` 缺失返回 `null`；正常值计算正确。
- **缓存下降**：首样本 `cacheDelta === null`；同模型下降 ≥ 4096 触发 `isCacheDrop`；跨模型不比较；大输入不因比值稀释而误报。
- **费用**：空闲/高峰单价选择正确；周末和法定节假日归为空闲；调休上班的周末仍归为空闲；旧模型名 `deepseek-v4-flash` 映射到 Flash 价格；未知模型返回 `null`；`inputTokens < cacheReadTokens` 时 `uncachedInput` 不为负。
- **时段**：`Asia/Shanghai` 时区转换正确（用固定 `Date` 构造测试用例，不依赖运行环境时区）。


## 11. 发布前检查清单

- [ ] `package.json` 中锁定 `@github/copilot-sdk` 的**精确版本**，README 中记录对应的 Copilot CLI 运行时版本。
- [ ] `joinSession` reject 时不阻塞扩展激活，状态栏显示“Copilot 会话不可用”。
- [ ] Webview CSP 正确配置，无 `unsafe-inline`。
- [ ] 状态栏和面板中明确标注“估算费用”，并说明与 Copilot 账单无关。
- [ ] README 声明 `cache_read_tokens` 字段映射风险及“命中率持续为 0”的排查建议。
- [ ] README 标注内置节假日数据的截止年份，提示跨年更新。
- [ ] `parentToolCallId` / `agentId` 双字段兼容读取。
- [ ] 会话 resume 后 `prevCacheByModel` 基线从 `globalState` 恢复。
- [ ] `assistant.usage` 事件处理中不抛出未捕获异常，避免单个异常样本中断采集。