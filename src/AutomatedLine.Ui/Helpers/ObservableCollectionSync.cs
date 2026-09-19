using System.Collections.ObjectModel;

namespace AutomatedLine.Ui.Helpers;

/// <summary>
/// 集合同步工具。
/// 定时刷新时若每次清空重建，会重置滚动位置与选中项，滚动条体验很差；
/// 这里按 Id 差量更新：已有的只刷新字段，新增的插入，消失的移除。
/// 列表规模很小（几十条），用简单的线性匹配即可，无需字典优化。
/// </summary>
public static class ObservableCollectionSync
{
    /// <summary>
    /// 按 Id 把目标集合同步为源集合的内容。
    /// </summary>
    /// <typeparam name="TItem">目标元素类型</typeparam>
    /// <typeparam name="TSource">源元素类型</typeparam>
    /// <param name="target">目标集合</param>
    /// <param name="source">源列表（已排序）</param>
    /// <param name="itemId">取目标元素 Id</param>
    /// <param name="sourceId">取源元素 Id</param>
    /// <param name="create">由源元素创建目标元素</param>
    /// <param name="update">用源元素刷新已有目标元素</param>
    public static void SyncById<TItem, TSource>(
        ObservableCollection<TItem> target,
        IReadOnlyList<TSource> source,
        Func<TItem, int> itemId,
        Func<TSource, int> sourceId,
        Func<TSource, TItem> create,
        Action<TItem, TSource> update)
    {
        // 1) 移除源中已不存在的元素
        for (var i = target.Count - 1; i >= 0; i--)
        {
            var id = itemId(target[i]);
            var stillExists = false;
            foreach (var s in source)
            {
                if (sourceId(s) == id)
                {
                    stillExists = true;
                    break;
                }
            }

            if (!stillExists)
            {
                target.RemoveAt(i);
            }
        }

        // 2) 按源顺序新增或刷新
        for (var i = 0; i < source.Count; i++)
        {
            var s = source[i];
            TItem? existing = default;
            var existingIndex = -1;
            for (var j = 0; j < target.Count; j++)
            {
                if (itemId(target[j]) == sourceId(s))
                {
                    existing = target[j];
                    existingIndex = j;
                    break;
                }
            }

            if (existing is null)
            {
                target.Insert(Math.Min(i, target.Count), create(s));
            }
            else
            {
                update(existing, s);

                // 顺序不一致时移动到正确位置，保证界面顺序稳定
                if (existingIndex != i && i < target.Count)
                {
                    target.Move(existingIndex, i);
                }
            }
        }
    }
}
