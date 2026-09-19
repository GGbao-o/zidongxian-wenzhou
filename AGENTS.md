## Git 提交规范

每次生成 commit message 时，必须：

1. 先执行 `git diff --staged` 和 `git log --oneline -5`
2. 按以下格式生成：

<type>(<scope>): <主题>

[why] 动机/根因
	[what] 改动文件/类
	[how] 技术方案

3. type: feat/fix/refactor/perf/style/test/docs/chore
4. [why] 禁止复述主题，必须写根因或业务动机
5. [what] 必须列具体文件/类名
6. [how] 必须说明技术方案
7. 混合改动拆成多个 commit





## 注释规范

### 必须写注释的地方
- **每个 public 类**：用 XML 文档注释 `/// <summary>` 说明职责
- **每个 public 方法**：`/// <summary>` 说明做什么，`<param>`/`<returns>` 说明参数和返回
- **关键私有方法**：逻辑复杂或非直观的，用 `//` 说明意图
- **关键字段/属性**：业务含义不直观的，用 `///` 或 `//` 说明
- **复杂逻辑块**：在 if/循环/算法前用 `//` 说明"为什么这么做"

### 注释要求
- 注释解释 **Why（为什么）** 和 **意图**，不是复述代码
- 中文注释
- 禁止无意义注释（如 `// i++`）
- 公开 API 的 XML 注释必须完整，会生成文档
- 修改代码时同步更新相关注释

### 示例
```csharp
/// <summary>
/// 订单库存扣减服务。
/// 负责在高并发场景下保证库存不被超卖。
/// </summary>
public class InventoryService
{
    /// <summary>
    /// 原子扣减指定 SKU 的库存。
    /// </summary>
    /// <param name="skuId">商品 SKU 标识</param>
    /// <param name="qty">扣减数量，必须为正数</param>
    /// <returns>扣减成功返回 true；库存不足或 SKU 不存在返回 false</returns>
    public async Task<bool> DeductAsync(long skuId, int qty)
    {
        // 用数据库原子操作替代"查-改-存"，避免并发竞态
        // 关键：WHERE 条件里带 Stock >= qty，靠受影响行数判断结果
        var affected = await _repo.DecreaseStockAsync(skuId, qty);
        return affected > 0;
    }
}