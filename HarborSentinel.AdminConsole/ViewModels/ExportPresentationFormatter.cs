using HarborSentinel.AdminContracts.Exports;
using System.Globalization;

namespace HarborSentinel.AdminConsole.ViewModels;

internal static class ExportPresentationFormatter
{
    public static string TranslateStatus(AdminExportJobStatusDto status)
    {
        return status switch
        {
            AdminExportJobStatusDto.Pending => "Ожидает запуска",
            AdminExportJobStatusDto.Running => "Выполняется",
            AdminExportJobStatusDto.Completed => "Завершена",
            AdminExportJobStatusDto.Failed => "Ошибка",
            _ => status.ToString()
        };
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} Б";

        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.0} КБ";

        return $"{bytes / (1024d * 1024d):0.0} МБ";
    }

    public static ExportJobListItemViewModel ToListItem(AdminExportJobListItemDto item)
    {
        return new ExportJobListItemViewModel
        {
            JobId = item.JobId,
            Label = item.Label,
            Status = TranslateStatus(item.Status),
            ScopeSummary = item.ScopeSummary,
            RequestedAtUtc = ChatPresentationFormatter.FormatTimestamp(item.RequestedAtUtc),
            CompletedAtUtc = ChatPresentationFormatter.FormatTimestamp(item.CompletedAtUtc),
            ResultSummary = string.IsNullOrWhiteSpace(item.ResultSummary) ? "нет данных" : item.ResultSummary,
            ErrorSummary = string.IsNullOrWhiteSpace(item.ErrorMessage) ? "нет" : item.ErrorMessage,
            HasDownload = item.HasDownload
        };
    }
}
