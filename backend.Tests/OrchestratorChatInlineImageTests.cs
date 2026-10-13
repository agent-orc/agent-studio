using System.IO;
using System.Linq;
using System.Text.Json;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression coverage for the orchestrator-chat multimodal fast path: when
/// the user pastes an image into the composer, the frontend ships the bytes
/// as base64 in the same POST. The backend lifts those bytes onto the Claude
/// CLI as an <c>image</c> content block in the stream-json user envelope,
/// so the model sees the picture in the same turn as the text - no Read
/// tool call required.
///
/// Three contracts pinned here:
///
///   1. <c>OrchestratorChat.ExtractInlineImages</c> turns the request-level
///      attachment carriers into the typed <see cref="CliOneShotImage"/>
///      list the runner consumes (null when nothing is inline, skips
///      malformed entries).
///   2. <c>OrchestratorChat.StripInlineBytes</c> drops the inline bytes
///      before the turn is persisted to <c>orchestrator-chat.jsonl</c>: the
///      audit log stays text-only, and the persisted attachment list keeps
///      the alt + relative path for re-rendering.
///   3. <c>ClaudeOneShot.BuildStreamJsonUserMessage</c> emits the exact
///      Anthropic SDK shape (<c>{"type":"user","message":{"role":"user",
///      "content":[{"type":"text",...},{"type":"image","source":{...}}]}}</c>)
///      followed by a single newline so the CLI sees it as one NDJSON line.
/// </summary>
public class OrchestratorChatInlineImageTests
{
    [Fact]
    public void ExtractInlineImages_LiftsBase64IntoCliOneShotImages()
    {
        var atts = new[]
        {
            new OrchestratorChatAttachment
            {
                Alt = "screenshot.png",
                RelativePath = "chat-attachments/abc123.png",
                InlineBase64 = "AAAA",
                MimeType = "image/png"
            },
            new OrchestratorChatAttachment
            {
                Alt = "no-bytes.png",
                RelativePath = "chat-attachments/def456.png"
            }
        };

        var images = OrchestratorChatService.ExtractInlineImages(atts);

        Assert.NotNull(images);
        Assert.Single(images!);
        Assert.Equal("AAAA", images![0].Base64);
        Assert.Equal("image/png", images[0].MediaType);
    }

    [Fact]
    public void ExtractInlineImages_ReturnsNull_WhenNothingInline()
    {
        var atts = new[]
        {
            new OrchestratorChatAttachment
            {
                Alt = "uploaded.png",
                RelativePath = "chat-attachments/ghi789.png"
            }
        };

        Assert.Null(OrchestratorChatService.ExtractInlineImages(atts));
        Assert.Null(OrchestratorChatService.ExtractInlineImages(null));
    }

    [Fact]
    public void ExtractInlineImages_SkipsNonImageMime()
    {
        var atts = new[]
        {
            new OrchestratorChatAttachment
            {
                Alt = "doc.pdf",
                RelativePath = "chat-attachments/doc.pdf",
                InlineBase64 = "AAAA",
                MimeType = "application/pdf"
            }
        };

        Assert.Null(OrchestratorChatService.ExtractInlineImages(atts));
    }

    [Fact]
    public void ExtractInlineImages_DefaultsMimeToPng()
    {
        var atts = new[]
        {
            new OrchestratorChatAttachment
            {
                Alt = "screenshot",
                RelativePath = "chat-attachments/x.png",
                InlineBase64 = "AAAA"
            }
        };

        var images = OrchestratorChatService.ExtractInlineImages(atts);

        Assert.NotNull(images);
        Assert.Equal("image/png", images![0].MediaType);
    }

    [Fact]
    public void StripInlineBytes_KeepsAltAndRelativePath_DropsBase64AndMime()
    {
        var turn = new OrchestratorChatTurn
        {
            Role = OrchestratorChatRoles.User,
            Text = "what do you see?",
            Attachments = new System.Collections.Generic.List<OrchestratorChatAttachment>
            {
                new()
                {
                    Alt = "screenshot.png",
                    RelativePath = "chat-attachments/abc.png",
                    InlineBase64 = "very-long-base64-string-here",
                    MimeType = "image/png"
                }
            }
        };

        var stripped = OrchestratorChat.StripInlineBytes(turn);

        Assert.NotNull(stripped.Attachments);
        Assert.Single(stripped.Attachments!);
        Assert.Equal("screenshot.png", stripped.Attachments![0].Alt);
        Assert.Equal("chat-attachments/abc.png", stripped.Attachments[0].RelativePath);
        Assert.Null(stripped.Attachments[0].InlineBase64);
        Assert.Null(stripped.Attachments[0].MimeType);
    }

    [Fact]
    public void StripInlineBytes_ReturnsInputUnchanged_WhenNoInlineBytes()
    {
        var turn = new OrchestratorChatTurn
        {
            Role = OrchestratorChatRoles.User,
            Text = "no image here",
            Attachments = new System.Collections.Generic.List<OrchestratorChatAttachment>
            {
                new() { Alt = "uploaded.png", RelativePath = "chat-attachments/u.png" }
            }
        };

        var stripped = OrchestratorChat.StripInlineBytes(turn);

        Assert.Same(turn, stripped);
    }

    [Fact]
    public void BuildStreamJsonUserMessage_EmitsAnthropicEnvelope()
    {
        var images = new[]
        {
            new CliOneShotImage("AAAA", "image/png")
        };

        var line = ClaudeOneShot.BuildStreamJsonUserMessage("Describe the image", images);

        Assert.EndsWith("\n", line);
        using var doc = JsonDocument.Parse(line.TrimEnd('\n'));
        var root = doc.RootElement;
        Assert.Equal("user", root.GetProperty("type").GetString());
        var content = root.GetProperty("message").GetProperty("content");
        Assert.Equal(2, content.GetArrayLength());
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("Describe the image", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        var source = content[1].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal("AAAA", source.GetProperty("data").GetString());
    }

    [Fact]
    public void BuildStreamJsonUserMessage_DefaultsMissingMediaTypeToPng()
    {
        var images = new[] { new CliOneShotImage("AAAA", "") };

        var line = ClaudeOneShot.BuildStreamJsonUserMessage("hi", images);

        using var doc = JsonDocument.Parse(line.TrimEnd('\n'));
        var src = doc.RootElement.GetProperty("message").GetProperty("content")[1].GetProperty("source");
        Assert.Equal("image/png", src.GetProperty("media_type").GetString());
    }

    [Fact]
    public void Append_PersistsTurnWithoutInlineBase64()
    {
        var tempWatch = Path.Combine(Path.GetTempPath(), $"orch-chat-inline-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWatch);
        try
        {
            var chat = new OrchestratorChat(new Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorChat>());

            var turn = new OrchestratorChatTurn
            {
                Role = OrchestratorChatRoles.User,
                Text = "look at this",
                Attachments = new System.Collections.Generic.List<OrchestratorChatAttachment>
                {
                    new()
                    {
                        Alt = "screenshot.png",
                        RelativePath = "chat-attachments/abc.png",
                        InlineBase64 = new string('A', 5000), // bulky on purpose
                        MimeType = "image/png"
                    }
                }
            };

            Assert.True(chat.Append(tempWatch, turn));

            var jsonl = Path.Combine(tempWatch, ".orchestrator", "orchestrator-chat.jsonl");
            var content = File.ReadAllText(jsonl);
            Assert.DoesNotContain("inlineBase64", content, System.StringComparison.Ordinal);
            Assert.DoesNotContain("mimeType", content, System.StringComparison.Ordinal);
            Assert.DoesNotContain(new string('A', 100), content, System.StringComparison.Ordinal);
            Assert.Contains("chat-attachments/abc.png", content, System.StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempWatch, true); } catch { /* best-effort */ }
        }
    }
}
