using System.Net;
using System.Text;

namespace AgentStudio.Magazine;

/// <summary>Standalone article document for browser reading and print-to-PDF.</summary>
public static class WeeklyIssueHtml
{
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");
    public static string Render(WeeklyIssue issue)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\" data-document-pattern=\"article\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(H(issue.Masthead)).Append(" | Article document</title><style data-article-template=\"v2\">")
            .Append(Styles).Append("</style></head><body><main class=\"article\">");
        html.Append("<header class=\"page\"><p class=\"kicker\">Agent Studio · ").Append(H(issue.Kind))
            .Append("</p><h1>").Append(H(issue.Masthead)).Append("</h1><p class=\"lede\">")
            .Append(H(issue.CoverageLine)).Append("</p><p class=\"meta\">Issue ")
            .Append(H(issue.Id)).Append(" · ").Append(H(issue.PeriodStart.ToString("d MMM yyyy")))
            .Append(" to ").Append(H(issue.PeriodEnd.ToString("d MMM yyyy"))).Append(" · ")
            .Append(H(issue.PublishedAtUtc.ToString("u"))).Append("</p></header>");
        html.Append("<section aria-labelledby=\"front\"><h2 id=\"front\">Front page</h2>");
        if (issue.Lead is { } lead)
        {
            html.Append("<article class=\"lead\">"); Story(html, lead, true);
            if (!string.IsNullOrWhiteSpace(issue.LeadParagraph)) html.Append("<p>").Append(H(issue.LeadParagraph)).Append("</p>");
            html.Append("</article>");
        }
        foreach (var side in issue.SideStories) { html.Append("<article class=\"side\">"); Story(html, side, false); html.Append("</article>"); }
        html.Append("</section>");
        if (issue.Kind == "weekly")
        {
            html.Append("<section><h2>Decisions needed</h2>");
            foreach (var decision in issue.DecisionsNeeded) { html.Append("<article>"); Story(html, decision, false);
                if (decision.RecommendedOption is { } option) html.Append("<p>Recommended: ").Append(H(option)).Append("</p>");
                if (decision.DeadlineUtc is { } due) html.Append("<p>Due: ").Append(H(due.ToString("u"))).Append("</p>");
                html.Append("</article>"); }
            html.Append("</section><section><h2>Operations watch</h2><table><thead><tr><th>Area</th><th>Fact</th><th>State</th><th>Importance</th></tr></thead><tbody>");
            foreach (var row in issue.OperationsWatch)
                html.Append("<tr><td>").Append(H(row.Group)).Append("</td><td>").Append(H(row.Label))
                    .Append("</td><td>").Append(H(row.State)).Append("</td><td class=\"number\">")
                    .Append(row.Importance).Append("</td></tr>");
            html.Append("</tbody></table></section>");
            foreach (var section in issue.Sections)
            {
                html.Append("<section><h2>").Append(H(section.Role)).Append(" <span class=\"number\">")
                    .Append(section.Count).Append("</span></h2>");
                if (!string.IsNullOrWhiteSpace(section.Teaser))
                    html.Append("<p>").Append(H(section.Teaser)).Append("</p>");
                foreach (var story in section.Stories) { html.Append("<article>"); Story(html, story, false); html.Append("</article>"); }
                html.Append("</section>");
            }
            if (issue.Articles.Count > 0)
            {
                html.Append("<section><h2>Articles</h2>");
                foreach (var article in issue.Articles)
                    html.Append("<article><h3>").Append(H(article.Title)).Append("</h3><p class=\"byline\">By ")
                        .Append(H(article.Persona)).Append(" · ").Append(H(article.Role))
                        .Append(" · revision ").Append(article.Revision).Append("</p><p>")
                        .Append(H(article.Summary)).Append("</p></article>");
                html.Append("</section>");
            }
            html.Append("<section><h2>Trends and numbers</h2><table><thead><tr><th>Week</th><th>Raised</th><th>Accepted</th><th>Tokens used</th><th>Pool use</th><th>Cards delivered</th><th>USD per accepted finding</th></tr></thead><tbody>");
            foreach (var week in issue.Trends)
                html.Append("<tr><td>").Append(H(week.Week.ToString("d MMM"))).Append("</td><td>").Append(week.Raised)
                    .Append("</td><td>").Append(week.Accepted).Append("</td><td>").Append(week.TokensUsed)
                    .Append("</td><td>").Append(week.BudgetUsePercent?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")
                    .Append("</td><td>").Append(week.CardsDelivered).Append("</td><td>")
                    .Append(week.CostPerAcceptedFinding?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a")
                    .Append("</td></tr>");
            html.Append("</tbody></table></section><section><h2>Corrections</h2>");
            foreach (var correction in issue.Corrections) { html.Append("<article>"); Story(html, correction, false); html.Append("</article>"); }
            html.Append("</section><section><h2>Colophon</h2><ul>");
            foreach (var entry in issue.Colophon) html.Append("<li>").Append(H(entry)).Append("</li>");
            html.Append("</ul></section>");
        }
        return html.Append("</main></body></html>").ToString();
    }

    public static string SendSummary(WeeklyIssue issue, string signInLink)
    {
        if (!Uri.TryCreate(signInLink, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Sign-in link must use HTTPS.");
        var lines = new List<string> { issue.Masthead, issue.CoverageLine };
        foreach (var story in new[] { issue.Lead }.Concat(issue.SideStories).OfType<IssueStory>())
            lines.Add($"{story.Importance} · {story.Title}");
        lines.Add(signInLink);
        return string.Join("\n", lines);
    }

    private static void Story(StringBuilder html, IssueStory story, bool lead)
    {
        html.Append(lead ? "<h3>" : "<h3>").Append(H(story.Title)).Append(" <span class=\"number\">")
            .Append(story.Importance).Append("</span></h3><p class=\"byline\">By ")
            .Append(H(story.Persona)).Append(" · ").Append(H(story.Role)).Append(" · ")
            .Append(H(story.Id)).Append("</p>");
        if (!string.IsNullOrWhiteSpace(story.Teaser)) html.Append("<p>").Append(H(story.Teaser)).Append("</p>");
    }

    private const string Styles = """
        :root{color-scheme:light;--article-paper:#fcfcfb;--article-surface:#f2f1ee;--article-ink:#151515;--article-muted:#5f5e59;--article-line:#d8d6cf;--article-link:#1f67b2;--article-serif:Georgia,'Times New Roman',serif;--article-sans:system-ui,sans-serif}
        @media(prefers-color-scheme:dark){:root{color-scheme:dark;--article-paper:#1a1a19;--article-surface:#232322;--article-ink:#f7f7f4;--article-muted:#c3c2b7;--article-line:#3d3d3a;--article-link:#78aef0}}
        *{box-sizing:border-box}html,body{background:var(--article-paper);color:var(--article-ink)}body{margin:0;font:17px/1.7 var(--article-serif)}main.article{width:min(76ch,calc(100% - 40px));margin:auto;padding:0 0 64px}.page{padding:42px 0 20px;border-bottom:3px double var(--article-ink)}h1{font:700 clamp(2.4rem,7vw,4rem)/1.04 var(--article-serif);margin:0}h2{font:700 1.5rem/1.2 var(--article-sans);margin:40px 0 12px;border-bottom:1px solid var(--article-line);padding-bottom:8px}h3{font-size:1.2rem;line-height:1.25;margin:0 0 3px}.lead h3{font-size:2rem}.kicker,.meta,.byline,.number,th,td{font-family:var(--article-sans)}.kicker{font-size:.75rem;letter-spacing:.1em;text-transform:uppercase}.meta,.byline{color:var(--article-muted);font-size:.78rem}.number{font-size:.8em;font-variant-numeric:tabular-nums;color:var(--article-muted)}article{padding:14px 0;border-bottom:1px solid var(--article-line);break-inside:avoid}article p{margin:6px 0}table{width:100%;border-collapse:collapse;font-size:.83rem}th,td{padding:7px;text-align:left;border-bottom:1px solid var(--article-line);vertical-align:top}th{color:var(--article-muted)}a{color:var(--article-link)}@media(max-width:600px){table{display:block;overflow-x:auto}}@media print{@page{size:A4;margin:17mm}html,body{color:#000;background:#fff}main.article{width:100%;padding:0}section{break-inside:avoid}h2{break-after:avoid}article{break-inside:avoid}}
        """;
}
