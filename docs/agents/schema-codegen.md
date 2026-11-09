# Schema 代码生成计划

上级：[AGENTS.md](../../AGENTS.md) · 状态：**已批准，未开工**（第 1 期起可独立交付/回退）
相关：[存储、聚合与迁移](storage.md)、[工程约束](engineering.md)

## 1. 结论摘要

| 层 | 能否 JSON 驱动 | 覆盖量 |
| --- | --- | --- |
| **结构层**：DDL、列清单、参数绑定、行读取、迁移补齐 | ✅ 可行，收益大 | 10 表 / 162 列 / 599 处序号读取 / 9 处 INSERT / 53 处 `EnsureColumn` |
| **查询层**：82 条 SELECT 的业务语义、28 处动态 WHERE 拼接 | ❌ 不可行 | 只生成列名常量，SQL 本体仍手写 |

即：这是**结构演进的优化**，不是功能开发成本的优化。改表结构会明显更容易，改查询逻辑不会。

## 2. 现状（实测）

| 指标 | 数值 |
| --- | --- |
| `reader.GetString/GetInt64/GetDouble/IsDBNull` | 607 处，其中 **599 处用字面量序号**（98.7%） |
| `AddParameter` / `Parameters["@x"]` | 286 处（`AddParameter` 声明 + 赋值） |
| `CommandText` 赋值 | 82 处（含 28 处 `{xxxFilter}` 动态拼接） |
| `INSERT` | 9 处（`spans` / `session_events` / `agent_runs` / `metric_points` / 5 张小表） |
| `UPDATE` | 5 处 |
| `EnsureColumn` | 53 处（历史迁移轨迹） |
| 表 / 列 | 10 表 / 162 列（`spans` 35、`session_events` 34、`agent_runs` 29、`metric_points` 23、`model_prices` 20、其余 6 表 21 列） |

**核心痛点**：`SELECT` 中间插一列，后面所有 `reader.GetXxx(N)` 静默错位——编译不报错、测试可能不报错、数据就是错的。

列名跨层重复次数：

| 列 | 出现次数 |
| --- | --- |
| `cache_read_tokens` | 25 |
| `finish_reasons` | 15 |
| `hook_result_kind` | 9 |
| `permission_kind` | 7 |

加一列目前要同步改 7 处（DDL / INSERT 列 / VALUES / `AddParameter` / `Parameters` 赋值 / SELECT / reader）。

## 3. 实现方式选择

| 方式 | 机制 | 优点 | 缺点 | 结论 |
| --- | --- | --- | --- | --- |
| **A. 增量生成器** | `schema/*.json` 作为 `AdditionalFiles`，`IIncrementalGenerator` 生成到 `obj/` | 无独立构建步骤；改 JSON 即时生效；AOT 无影响（纯编译期） | 生成代码不进 git，需 `EmitCompilerGeneratedFiles` 才能调试 | **采用** |
| B. 控制台工具 | `dotnet run --project tools/SchemaGen`，生成结果提交 | 可 review、可 diff | 忘记重跑会漂移；多一个项目 | 备选 |
| T4 模板 | `.tt` | 无需写 Roslyn | VS 专属，Linux/macOS CI 跑不了（`server-release.yml` 有 linux + macOS 矩阵） | **否决** |
| Dapper.AOT / EF Core | 第三方生成器 | 现成 | 只支持其方言子集；EF/Dapper 经典版破坏 AOT（`IL2026`/`IL3050`） | **否决** |

选择 A 的理由：仓库红线是「附加式迁移、不能丢数据」，把 schema 当唯一真源、生成物不提交，天然阻止手改生成代码；reviewer 看 JSON diff 就知道加了什么列，比读生成代码更清楚。

## 4. 分四期，每期独立可回退

### 第 1 期：schema JSON + 列名常量（约 0.5 天）

`schema/tables.json`：

```json
{
  "tables": {
    "spans": {
      "columns": [
        { "name": "trace_id", "type": "TEXT", "notNull": true },
        { "name": "is_main", "type": "INTEGER", "notNull": true, "default": 0 },
        { "name": "ttft_ms", "type": "INTEGER" }
      ],
      "primaryKey": ["trace_id", "span_id"]
    }
  }
}
```

产出：

- `Spans.Columns.TraceId`（`const int` 序号，等于 `SELECT` 中的位置）
- `Spans.Names.TraceId`（`const string` 列名，用于 DDL / 参数名）

**闸门**：把一处 `reader.GetInt64(16)` 换成 `reader.GetInt64(Spans.Columns.InputTokens)`，`dotnet test` 94 项全绿 → 证明常量与真实序号对齐。

### 第 2 期：生成 DDL 与迁移补齐（约 1 天）

- `CREATE TABLE IF NOT EXISTS` 由 schema 生成。
- `EnsureColumn` 由 **schema 历史快照**（v1 / v2 / v3）差分生成，**不是**用当前 schema 反推——现有 53 处 `EnsureColumn` 是历史轨迹，凭当前 schema 重算会漏掉中间版本，旧库升级直接缺列。

**闸门**：拿 `user_version=3` 的现网库副本启动新构建，`PRAGMA table_info` 对 10 张表逐列比对**完全一致**，行数不变、索引不变。任一列差异 → 回退。

### 第 3 期：生成 `Insert` 与 `Read`（约 1 天）

只为 `spans`(35 列) 与 `session_events`(34 列) 生成（这两张表占序号读取的 62%）：

```csharp
internal static void BindSpan(SqliteCommand cmd, OtlpSpanRecord span);
internal static OtlpSpanRecord ReadSpan(SqliteDataReader reader);
```

**闸门**：94 单测 + 53 冒烟全绿；`dotnet publish -r win-x64` 的 AOT 耗时与产物大小无退化；`publish` 目录中不存在生成器 dll。

### 第 4 期：剩余 8 表 + 收尾（约 0.5 天）

`agent_runs`(29) / `metric_points`(23) / `model_prices`(20) / 6 张小表。

**总计约 3 天**，可停在第 1 或第 2 期。

## 5. 生成器工程约束

```xml
<!-- 引用方式：绝不能把它打进 AOT 产物 -->
<ProjectReference Include="..\TokenTracker.Generators\TokenTracker.Generators.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```

- 生成器项目 `netstandard2.0`，只引 `Microsoft.CodeAnalysis.CSharp`（`PrivateAssets="all"`）。
- 必须用 `IIncrementalGenerator`；`ISourceGenerator` 会让 15k 行项目的增量编译变慢。
- 调试：`EmitCompilerGeneratedFiles=true` + `CompilerGeneratedFilesOutputPath`。
- 生成器不得引用 `TokenTracker.Core`（循环依赖）；模型形状用 JSON + 命名约定描述。

## 6. 明确不做

- **不生成 82 条 SELECT**：含窗口重建、`OR` 形式走索引的区间查询、多轮递归回溯、`GROUP BY` 聚合，语义无法用 JSON 表达。生成器只提供列名常量。
- **不生成 28 处动态 WHERE**：`{sourceFilter}` 这类插值是索引可用性的关键（见[存储](storage.md)第 2 节），生成器介入只会破坏它。
- **不引入 Dapper / EF Core**：破坏 AOT。
- **不改已有列顺序**：新列一律追加到末尾，与现有 `EnsureColumn` 行为一致。

## 7. 风险与对策

| 风险 | 对策 |
| --- | --- |
| 生成器被打进 AOT 产物 | `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`；publish 后检查目录无生成器 dll |
| 历史迁移轨迹丢失，旧库升级缺列 | 第 2 期闸门强制用现网库副本验证 `table_info` 逐列一致 |
| 生成代码与手写 SQL 不一致 | 单测断言「SELECT 列数 == 生成的行读取字段数」 |
| 列序变化导致历史数据错读 | 只从 schema 生成序号，不改列顺序，新列追加末尾 |

## 8. 低风险替代（不引入生成器）

若暂时不建生成器项目，可先做**零依赖重构**：把 4 个文件里「`AddParameter` 一串 + `Parameters["@x"].Value` 一串 + `reader.IsDBNull(i) ? ... : ...`」抽成每表一对静态方法（`BindSpan` / `ReadSpan`），能去掉约 400 行重复，不改构建链、不动 schema。缺点仍是列序靠人工同步。

先在单表（`spans`）落地，用 94 单测回归，作为第 3 期的"手写基线"参考。