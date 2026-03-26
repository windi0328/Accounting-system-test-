using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<TransactionRepository>();

var app = builder.Build();

app.UseStaticFiles();

var bundledWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (Directory.Exists(bundledWebRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(bundledWebRoot)
    });
}

app.MapGet("/", (HttpContext context, TransactionRepository repository) =>
{
    var transactions = repository.GetAll();
    var summary = ReportBuilder.BuildSummary(transactions);
    var recentTransactions = transactions
        .OrderByDescending(transaction => transaction.CreatedAt)
        .Take(6)
        .ToList();

    var content = HtmlPages.RenderDashboard(
        summary,
        recentTransactions,
        context.Request.Query["notice"],
        context.Request.Query["error"]);

    return Results.Content(HtmlPages.WithLayout("記帳儀表板", "/", content), "text/html; charset=utf-8");
});

app.MapPost("/transactions", async (HttpContext context, TransactionRepository repository) =>
{
    var form = await context.Request.ReadFormAsync();
    var typeInput = form["type"].ToString();
    var category = form["category"].ToString().Trim();
    var note = form["note"].ToString().Trim();

    if (!Enum.TryParse<TransactionType>(typeInput, true, out var type))
    {
        return Results.Redirect("/?error=" + Uri.EscapeDataString("交易類型不正確。"));
    }

    if (string.IsNullOrWhiteSpace(category))
    {
        return Results.Redirect("/?error=" + Uri.EscapeDataString("項目名稱不可為空。"));
    }

    if (!decimal.TryParse(form["amount"], CultureInfo.InvariantCulture, out var amount) && !decimal.TryParse(form["amount"], out amount))
    {
        return Results.Redirect("/?error=" + Uri.EscapeDataString("金額格式不正確。"));
    }

    if (amount <= 0)
    {
        return Results.Redirect("/?error=" + Uri.EscapeDataString("金額必須大於 0。"));
    }

    repository.Add(new Transaction
    {
        Id = Guid.NewGuid(),
        Type = type,
        Category = category,
        Amount = amount,
        Note = note,
        CreatedAt = DateTime.Now
    });

    return Results.Redirect("/?notice=" + Uri.EscapeDataString("交易已成功新增。"));
});

app.MapGet("/transactions", (TransactionRepository repository) =>
{
    var transactions = repository.GetAll()
        .OrderByDescending(transaction => transaction.CreatedAt)
        .ToList();

    var content = HtmlPages.RenderTransactions(transactions);
    return Results.Content(HtmlPages.WithLayout("所有紀錄", "/transactions", content), "text/html; charset=utf-8");
});

app.MapGet("/reports/monthly", (TransactionRepository repository) =>
{
    var monthlyReports = ReportBuilder.BuildMonthlyReports(repository.GetAll());
    var content = HtmlPages.RenderMonthlyReport(monthlyReports);
    return Results.Content(HtmlPages.WithLayout("每月報表", "/reports/monthly", content), "text/html; charset=utf-8");
});

app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();

internal sealed class TransactionRepository
{
    private readonly string _dataFilePath;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();

    public TransactionRepository(IHostEnvironment environment)
    {
        var dataDirectory = Path.Combine(environment.ContentRootPath, "Data");
        Directory.CreateDirectory(dataDirectory);
        _dataFilePath = Path.Combine(dataDirectory, "transactions.json");
    }

    public List<Transaction> GetAll()
    {
        lock (_syncRoot)
        {
            if (!File.Exists(_dataFilePath))
            {
                return [];
            }

            try
            {
                var json = File.ReadAllText(_dataFilePath);
                return JsonSerializer.Deserialize<List<Transaction>>(json, _jsonOptions) ?? [];
            }
            catch
            {
                return [];
            }
        }
    }

    public void Add(Transaction transaction)
    {
        lock (_syncRoot)
        {
            var transactions = GetAll();
            transactions.Add(transaction);

            var json = JsonSerializer.Serialize(transactions, _jsonOptions);
            File.WriteAllText(_dataFilePath, json);
        }
    }
}

internal static class ReportBuilder
{
    public static SummaryViewModel BuildSummary(IEnumerable<Transaction> transactions)
    {
        var transactionList = transactions.ToList();
        var income = transactionList.Where(transaction => transaction.Type == TransactionType.Income).Sum(transaction => transaction.Amount);
        var expense = transactionList.Where(transaction => transaction.Type == TransactionType.Expense).Sum(transaction => transaction.Amount);

        return new SummaryViewModel(
            income,
            expense,
            income - expense,
            transactionList.Count,
            transactionList
                .OrderByDescending(transaction => transaction.CreatedAt)
                .Take(6)
                .ToList());
    }

    public static List<MonthlyReport> BuildMonthlyReports(IEnumerable<Transaction> transactions)
    {
        return transactions
            .GroupBy(transaction => new DateOnly(transaction.CreatedAt.Year, transaction.CreatedAt.Month, 1))
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var items = group.OrderByDescending(transaction => transaction.CreatedAt).ToList();
                var income = items.Where(transaction => transaction.Type == TransactionType.Income).Sum(transaction => transaction.Amount);
                var expense = items.Where(transaction => transaction.Type == TransactionType.Expense).Sum(transaction => transaction.Amount);
                var topCategories = items
                    .Where(transaction => transaction.Type == TransactionType.Expense)
                    .GroupBy(transaction => transaction.Category)
                    .Select(categoryGroup => new CategorySummary(categoryGroup.Key, categoryGroup.Sum(transaction => transaction.Amount)))
                    .OrderByDescending(item => item.Amount)
                    .Take(5)
                    .ToList();

                return new MonthlyReport(group.Key, income, expense, items.Count, items, topCategories);
            })
            .ToList();
    }
}

internal static class HtmlPages
{
    public static string WithLayout(string title, string currentPath, string content)
    {
        var safeTitle = Encode(title);

        return $$"""
<!DOCTYPE html>
<html lang="zh-Hant">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{safeTitle}}</title>
  <link rel="stylesheet" href="/site.css">
</head>
<body>
  <div class="shell">
    <aside class="sidebar">
      <div class="brand">
        <div class="brand-mark">NT</div>
        <div>
          <div class="brand-title">NoteTrack</div>
          <div class="brand-subtitle">個人記帳網頁版</div>
        </div>
      </div>
      <nav class="nav">
        {{NavLink("/", "儀表板", currentPath)}}
        {{NavLink("/transactions", "所有紀錄", currentPath)}}
        {{NavLink("/reports/monthly", "每月報表", currentPath)}}
      </nav>
    </aside>
    <main class="content">
      {{content}}
    </main>
  </div>
</body>
</html>
""";
    }

    public static string RenderDashboard(
        SummaryViewModel summary,
        IReadOnlyList<Transaction> recentTransactions,
        string? notice,
        string? error)
    {
        var builder = new StringBuilder();

        builder.AppendLine("""
<section class="hero">
  <div>
    <p class="eyebrow">Daily Finance</p>
    <h1>把記帳搬進瀏覽器</h1>
    <p class="hero-copy">現在可以直接用 HTML 表單記錄收入與支出，首頁同時看到摘要與最近交易。</p>
  </div>
</section>
""");

        if (!string.IsNullOrWhiteSpace(notice))
        {
            builder.AppendLine($"""<div class="alert success">{Encode(notice!)}</div>""");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            builder.AppendLine($"""<div class="alert error">{Encode(error!)}</div>""");
        }

        builder.AppendLine($$"""
<section class="grid metrics">
  {{MetricCard("總收入", FormatCurrency(summary.Income), "income")}}
  {{MetricCard("總支出", FormatCurrency(summary.Expense), "expense")}}
  {{MetricCard("目前結餘", FormatCurrency(summary.Balance), summary.Balance >= 0 ? "balance" : "warning")}}
  {{MetricCard("交易筆數", summary.Count.ToString(CultureInfo.InvariantCulture), "neutral")}}
</section>

<section class="grid two-columns">
  <article class="panel">
    <div class="panel-header">
      <div>
        <p class="eyebrow">New Entry</p>
        <h2>新增交易</h2>
      </div>
    </div>
    <form method="post" action="/transactions" class="entry-form">
      <label>
        <span>交易類型</span>
        <select name="type" required>
          <option value="Income">收入</option>
          <option value="Expense">支出</option>
        </select>
      </label>
      <label>
        <span>項目名稱</span>
        <input type="text" name="category" placeholder="例如：薪水、餐費、交通" required>
      </label>
      <label>
        <span>金額</span>
        <input type="number" name="amount" step="0.01" min="0.01" placeholder="0" required>
      </label>
      <label>
        <span>備註</span>
        <textarea name="note" rows="4" placeholder="補充說明"></textarea>
      </label>
      <button type="submit">儲存交易</button>
    </form>
  </article>

  <article class="panel">
    <div class="panel-header">
      <div>
        <p class="eyebrow">Recent Activity</p>
        <h2>最近交易</h2>
      </div>
      <a class="text-link" href="/transactions">看全部</a>
    </div>
    {{RenderRecentTransactions(recentTransactions)}}
  </article>
</section>
""");

        return builder.ToString();
    }

    public static string RenderTransactions(IReadOnlyList<Transaction> transactions)
    {
        if (transactions.Count == 0)
        {
            return """
<section class="panel">
  <div class="panel-header">
    <div>
      <p class="eyebrow">Transactions</p>
      <h1>所有紀錄</h1>
    </div>
  </div>
  <p class="empty-state">目前還沒有任何交易，先回到首頁新增第一筆吧。</p>
</section>
""";
        }

        var rows = string.Join(
            Environment.NewLine,
            transactions.Select(transaction =>
                $$"""
<tr>
  <td>{{Encode(transaction.CreatedAt.ToString("yyyy-MM-dd HH:mm"))}}</td>
  <td><span class="badge {{(transaction.Type == TransactionType.Income ? "income" : "expense")}}">{{Encode(GetTypeLabel(transaction.Type))}}</span></td>
  <td>{{Encode(transaction.Category)}}</td>
  <td class="amount {{(transaction.Type == TransactionType.Income ? "income-text" : "expense-text")}}">{{FormatCurrency(transaction.Amount)}}</td>
  <td>{{Encode(string.IsNullOrWhiteSpace(transaction.Note) ? "-" : transaction.Note)}}</td>
</tr>
"""));

        return $$"""
<section class="panel">
  <div class="panel-header">
    <div>
      <p class="eyebrow">Transactions</p>
      <h1>所有紀錄</h1>
    </div>
  </div>
  <div class="table-wrap">
    <table>
      <thead>
        <tr>
          <th>日期</th>
          <th>類型</th>
          <th>項目</th>
          <th>金額</th>
          <th>備註</th>
        </tr>
      </thead>
      <tbody>
        {{rows}}
      </tbody>
    </table>
  </div>
</section>
""";
    }

    public static string RenderMonthlyReport(IReadOnlyList<MonthlyReport> reports)
    {
        if (reports.Count == 0)
        {
            return """
<section class="panel">
  <div class="panel-header">
    <div>
      <p class="eyebrow">Monthly Report</p>
      <h1>每月報表</h1>
    </div>
  </div>
  <p class="empty-state">目前沒有資料可供產生月報表。</p>
</section>
""";
        }

        var overviewRows = string.Join(
            Environment.NewLine,
            reports.Select(report =>
                $$"""
<tr>
  <td>{{Encode(report.Month.ToString("yyyy-MM"))}}</td>
  <td class="income-text">{{FormatCurrency(report.Income)}}</td>
  <td class="expense-text">{{FormatCurrency(report.Expense)}}</td>
  <td class="{{(report.Balance >= 0 ? "balance-text" : "warning-text")}}">{{FormatCurrency(report.Balance)}}</td>
  <td>{{report.Count}}</td>
</tr>
"""));

        var focus = reports[0];
        var categoryItems = focus.TopExpenseCategories.Count == 0
            ? """<p class="empty-state">這個月份還沒有支出分類資料。</p>"""
            : string.Join(
                Environment.NewLine,
                focus.TopExpenseCategories.Select(item =>
                {
                    var width = focus.TopExpenseCategories.Max(category => category.Amount) == 0
                        ? 0
                        : Math.Round((double)(item.Amount / focus.TopExpenseCategories.Max(category => category.Amount) * 100m), 0);

                    return $$"""
<div class="bar-row">
  <div class="bar-label">{{Encode(item.Name)}}</div>
  <div class="bar-track"><div class="bar-fill" style="width: {{width}}%;"></div></div>
  <div class="bar-value">{{FormatCurrency(item.Amount)}}</div>
</div>
""";
                }));

        return $$"""
<section class="grid monthly-grid">
  <article class="panel">
    <div class="panel-header">
      <div>
        <p class="eyebrow">Monthly Report</p>
        <h1>月份總覽</h1>
      </div>
    </div>
    <div class="table-wrap">
      <table>
        <thead>
          <tr>
            <th>月份</th>
            <th>收入</th>
            <th>支出</th>
            <th>淨額</th>
            <th>筆數</th>
          </tr>
        </thead>
        <tbody>
          {{overviewRows}}
        </tbody>
      </table>
    </div>
  </article>

  <article class="panel">
    <div class="panel-header">
      <div>
        <p class="eyebrow">Focus Month</p>
        <h2>{{Encode(focus.Month.ToString("yyyy-MM"))}}</h2>
      </div>
    </div>
    <div class="grid metrics compact">
      {{MetricCard("本月收入", FormatCurrency(focus.Income), "income")}}
      {{MetricCard("本月支出", FormatCurrency(focus.Expense), "expense")}}
      {{MetricCard("本月淨額", FormatCurrency(focus.Balance), focus.Balance >= 0 ? "balance" : "warning")}}
    </div>
    <div class="category-list">
      <h3>支出分類排行</h3>
      {{categoryItems}}
    </div>
  </article>
</section>
""";
    }

    private static string RenderRecentTransactions(IReadOnlyList<Transaction> transactions)
    {
        if (transactions.Count == 0)
        {
            return """<p class="empty-state">還沒有交易紀錄，新增第一筆後這裡就會開始有內容。</p>""";
        }

        var items = string.Join(
            Environment.NewLine,
            transactions.Select(transaction =>
                $$"""
<li class="activity-item">
  <div>
    <div class="activity-title">{{Encode(transaction.Category)}}</div>
    <div class="activity-meta">{{Encode(transaction.CreatedAt.ToString("yyyy-MM-dd HH:mm"))}} · {{Encode(GetTypeLabel(transaction.Type))}}</div>
  </div>
  <div class="amount {{(transaction.Type == TransactionType.Income ? "income-text" : "expense-text")}}">{{FormatCurrency(transaction.Amount)}}</div>
</li>
"""));

        return $$"""<ul class="activity-list">{{items}}</ul>""";
    }

    private static string MetricCard(string label, string value, string tone) =>
        $$"""
<article class="metric-card {{tone}}">
  <div class="metric-label">{{Encode(label)}}</div>
  <div class="metric-value">{{Encode(value)}}</div>
</article>
""";

    private static string NavLink(string href, string label, string currentPath)
    {
        var isActive = string.Equals(href, currentPath, StringComparison.OrdinalIgnoreCase);
        var activeClass = isActive ? "active" : string.Empty;
        return $$"""<a class="nav-link {{activeClass}}" href="{{href}}">{{Encode(label)}}</a>""";
    }

    private static string FormatCurrency(decimal amount) => $"NT$ {amount:N0}";

    private static string GetTypeLabel(TransactionType type) =>
        type == TransactionType.Income ? "收入" : "支出";

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}

internal sealed class Transaction
{
    public Guid Id { get; init; }

    public TransactionType Type { get; init; }

    public string Category { get; init; } = string.Empty;

    public decimal Amount { get; init; }

    public string Note { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }
}

internal enum TransactionType
{
    Income = 1,
    Expense = 2
}

internal sealed record SummaryViewModel(
    decimal Income,
    decimal Expense,
    decimal Balance,
    int Count,
    IReadOnlyList<Transaction> RecentTransactions);

internal sealed record MonthlyReport(
    DateOnly Month,
    decimal Income,
    decimal Expense,
    int Count,
    IReadOnlyList<Transaction> Transactions,
    IReadOnlyList<CategorySummary> TopExpenseCategories)
{
    public decimal Balance => Income - Expense;
}

internal sealed record CategorySummary(string Name, decimal Amount);
