using System.Globalization;
using System.Net;
using System.Text;

namespace AgentStudio.Magazine;

public static class PresenterDeckExport
{
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string Html(PresenterDeck deck)
    {
        var html = new StringBuilder("<!doctype html><html lang=\"en\" data-studio-theme=\"light\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>")
            .Append(H(deck.Kind == "weekly" ? "Weekly review" : "Monthly review"))
            .Append("</title><style>").Append(Styles).Append("</style></head><body><main aria-label=\"Presenter deck\">");
        foreach (var slide in deck.Slides)
        {
            html.Append("<section class=\"slide\" data-layout=\"").Append(H(slide.Layout))
                .Append("\" aria-label=\"").Append(H(slide.Title)).Append("\"><div class=\"slide-inner\"><p class=\"kicker\">Agent Studio · ")
                .Append(H(deck.Kind)).Append("</p><h1>").Append(H(slide.Title)).Append("</h1><div class=\"blocks\">");
            foreach (var block in slide.Blocks)
            {
                html.Append("<article><div><p class=\"block-type\">").Append(H(block.Kind == "item" ? block.Role ?? "Finding" : block.Kind == "watch" ? "Operations" : "Measure"))
                    .Append("</p><h2>").Append(H(block.Label)).Append("</h2>");
                if (block.ItemId is not null) html.Append("<p class=\"source\">").Append(H(block.ItemId)).Append("</p>");
                html.Append("</div>");
                if (block.Value is not null) html.Append("<strong class=\"value\">").Append(H(block.Value)).Append("</strong>");
                html.Append("</article>");
            }
            html.Append("</div><footer><span>").Append(H(deck.Audience)).Append("</span></footer></div></section>");
        }
        return html.Append("</main><script>let n=0;const slides=[...document.querySelectorAll('.slide')];function show(i){n=Math.max(0,Math.min(slides.length-1,i));slides.forEach((s,k)=>s.hidden=k!==n)}document.addEventListener('keydown',e=>{if(e.key==='ArrowRight'||e.key==='ArrowDown'||e.key==='PageDown'){show(n+1);e.preventDefault()}if(e.key==='ArrowLeft'||e.key==='ArrowUp'||e.key==='PageUp'){show(n-1);e.preventDefault()}});show(0)</script></body></html>").ToString();
    }

    // A compact vector PDF: one 960 x 540 page per slide, using the same flat hierarchy.
    public static byte[] Pdf(PresenterDeck deck)
    {
        var objects = new List<string>();
        int Add(string value) { objects.Add(value); return objects.Count; }
        var catalog = Add("");
        var pages = Add("");
        var font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var pageIds = new List<int>();
        foreach (var slide in deck.Slides)
        {
            var content = new StringBuilder("0.12 0.14 0.18 rg 0 0 960 540 re f\n1 1 1 rg\n");
            Text(content, 54, 475, 30, slide.Title);
            Text(content, 54, 510, 11, "AGENT STUDIO / " + deck.Kind.ToUpperInvariant());
            var y = 390;
            foreach (var block in slide.Blocks)
            {
                content.Append("0.18 0.20 0.25 rg 48 ").Append(y - 38).Append(" 864 92 re f\n1 1 1 rg\n");
                Text(content, 65, y + 23, 12, block.Kind == "item" ? block.Role ?? "Finding" : block.Kind);
                Text(content, 65, y - 7, 19, block.Label);
                if (block.ItemId is not null) Text(content, 65, y - 26, 10, block.ItemId);
                if (block.Value is not null) Text(content, 835, y - 8, 22, block.Value);
                y -= 112;
            }
            Text(content, 54, 25, 11, deck.Audience);
            var bytes = Encoding.Latin1.GetBytes(content.ToString());
            var stream = Add("<< /Length " + bytes.Length + " >>\nstream\n" + content + "endstream");
            pageIds.Add(Add("<< /Type /Page /Parent " + pages + " 0 R /MediaBox [0 0 960 540] /Resources << /Font << /F1 " + font + " 0 R >> >> /Contents " + stream + " 0 R >>"));
        }
        objects[pages - 1] = "<< /Type /Pages /Kids [" + string.Join(" ", pageIds.Select(id => id + " 0 R")) + "] /Count " + pageIds.Count + " >>";
        objects[catalog - 1] = "<< /Type /Catalog /Pages " + pages + " 0 R >>";
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        foreach (var (value, index) in objects.Select((value, index) => (value, index)))
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append(index + 1).Append(" 0 obj\n").Append(value).Append("\nendobj\n");
        }
        var xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root ").Append(catalog)
            .Append(" 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static void Text(StringBuilder pdf, int x, int y, int size, string value)
    {
        var safe = new string(value.Select(c => c is >= ' ' and <= '~' ? c : '?').Take(95).ToArray())
            .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        pdf.Append("BT /F1 ").Append(size).Append(" Tf ").Append(x).Append(' ').Append(y)
            .Append(" Td (").Append(safe).Append(") Tj ET\n");
    }

    private const string Styles = """
        :root{color-scheme:light;--studio-bg-editor:#fff;--studio-bg-elevated:#f5f5f3;--studio-fg-strong:#1a1a1a;--studio-fg-dim:#555;--studio-border:#d4d4d4;--studio-accent:#9b451f}
        [data-studio-theme='dark']{color-scheme:dark;--studio-bg-editor:#1e1e1e;--studio-bg-elevated:#292929;--studio-fg-strong:#fff;--studio-fg-dim:#bbb;--studio-border:#444;--studio-accent:#e48d6f}
        @media(prefers-color-scheme:dark){:root:not([data-studio-theme='light']){color-scheme:dark;--studio-bg-editor:#1e1e1e;--studio-bg-elevated:#292929;--studio-fg-strong:#fff;--studio-fg-dim:#bbb;--studio-border:#444;--studio-accent:#e48d6f}}
        *{box-sizing:border-box}html,body{margin:0;background:var(--studio-bg-editor);color:var(--studio-fg-strong);font-family:Inter,system-ui,sans-serif}.slide{width:100vw;height:100vh;display:grid;place-items:center}.slide[hidden]{display:none}.slide-inner{position:relative;width:min(100vw,177.777vh);aspect-ratio:16/9;background:var(--studio-bg-editor);padding:4%;border:1px solid var(--studio-border);overflow:hidden}.kicker,.block-type,.source,footer{color:var(--studio-fg-dim)}.kicker,.block-type{text-transform:uppercase;letter-spacing:.1em;font-size:clamp(10px,1.3vw,16px);font-weight:700}h1{font-size:clamp(30px,4.4vw,72px);margin:0 0 4%;line-height:1.1}.blocks{display:grid;gap:1.5%;height:62%}article{display:flex;align-items:center;justify-content:space-between;gap:3%;padding:1.5% 2.5%;background:var(--studio-bg-elevated);border:1px solid var(--studio-border)}article h2{font-size:clamp(18px,2vw,34px);line-height:1.2;margin:0}.block-type,.source{margin:0 0 .4em}.value{font-variant-numeric:tabular-nums;font-size:clamp(24px,3.5vw,56px);color:var(--studio-fg-strong)}footer{position:absolute;left:4%;right:4%;bottom:3%;display:flex;justify-content:space-between;font-size:clamp(10px,1vw,16px)}@media print{@page{size:16in 9in;margin:0}.slide,.slide[hidden]{display:block;width:16in;height:9in;break-after:page}.slide-inner{width:16in;height:9in;border:0}}
        """;
}
