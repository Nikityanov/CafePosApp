namespace CafePos.Core.Services;

/// <summary>Creates CSV reports for the operator (sales journal, shift report).</summary>
public interface IReportExportService
{
    /// <summary>One row per sold position, newest first.</summary>
    Task<string> ExportSalesCsvAsync(CancellationToken cancellationToken = default);

    Task<string> ExportShiftReportCsvAsync(Guid shiftId, CancellationToken cancellationToken = default);
}
