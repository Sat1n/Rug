using Rug.UI.Core.Models;
namespace Rug.UI.Helpers;
public static class TracePresentation
{
    public static string Boolean(bool? value)=>value is null?"未知":value.Value?"true":"false";
    public static string Integrity(TracePage page)=>page.FromDisk?
        $"{page.Status} · 完整性与任务结果分开；未封存 session 出现 Incomplete 是正常情况。"+
            (page.Status==TraceReadStatus.NotFound?" 关联轨迹不可用。":""):
        "当前保留窗口 · Incomplete（不是完整历史）";
}
