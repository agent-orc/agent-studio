using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// AGT-2993: a compose scenario review step builds about 2.6 GB of images plus
/// BuildKit cache before it runs anything. On 2026-09-28 the scenario and smoke
/// residue filled agent-runner-01 to 95-96 % twice. A review step started on an
/// almost full disk fails inside the build and reads like a product failure,
/// or fills the disk for every other slot on the host. The review executor
/// therefore logs the free space before such a step and refuses to start it
/// below the configured floor, as a typed infrastructure outcome instead.
/// </summary>
internal static class ComposeScenarioDiskAdmission
{
    /// <summary>Review infrastructure classification for a refused compose scenario.</summary>
    internal const string DiskLowClassification = "ComposeScenarioDiskLow";

    /// <summary>Default floor: refuse below 10 % free.</summary>
    internal const int DefaultMinFreePercent = 10;

    /// <summary>
    /// True when a deterministic review command executes a Docker Compose
    /// scenario or smoke: <c>scripts/scenario.sh --target compose</c> (any
    /// level) or <c>scripts/compose-smoke-test.sh</c>, run directly, through a
    /// shell (<c>sh -lc '...'</c>, <c>bash script</c>), inside a command
    /// substitution (<c>out=$(...)</c>, backticks) or behind environment
    /// assignments and <c>env</c>/<c>exec</c>/<c>timeout</c> wrappers. Only a
    /// command position counts: a mention inside quoted data, an argument of
    /// another program (<c>echo</c>, <c>grep</c>) or a shell comment does not,
    /// because refusing such a step on a low disk would block a review that
    /// never builds an image.
    /// </summary>
    internal static bool IsComposeScenario(ReviewCommandDto command)
    {
        if (ReviewCommandKinds.IsAgent(command.ExecutionKind))
            return false;
        return ExecutesComposeScenario([command.FileName, .. command.Arguments], depth: 0);
    }

    /// <summary>
    /// Pure admission decision for one compose scenario step. A floor of 0
    /// disables the refusal but keeps the log line; an unreadable disk is
    /// admitted, because the probe failing says nothing about the free space.
    /// </summary>
    internal static ComposeScenarioDiskDecision Decide(
        string path,
        long? freeBytes,
        long? totalBytes,
        int minFreePercent)
    {
        var floor = Math.Clamp(minFreePercent, 0, 100);
        if (freeBytes is not { } free || totalBytes is not { } total || total <= 0 || free < 0)
            return new ComposeScenarioDiskDecision(true, path, null, null, null, floor);
        var percent = Math.Round(100d * free / total, 1);
        var admit = floor == 0 || 100d * free / total >= floor;
        return new ComposeScenarioDiskDecision(admit, path, free, total, percent, floor);
    }

    private const int MaxShellNesting = 4;

    private static readonly HashSet<string> Shells =
        new(["sh", "bash", "dash", "ash", "ksh", "zsh"], StringComparer.Ordinal);

    // Words that may precede the program of a simple command without being it.
    private static readonly HashSet<string> ReservedWords =
        new(["!", "{", "}", "if", "then", "else", "elif", "fi", "do", "done", "while", "until", "time"],
            StringComparer.Ordinal);

    private static readonly HashSet<string> Wrappers =
        new(["env", "exec", "nohup", "time", "timeout"], StringComparer.Ordinal);

    private static bool ExecutesComposeScenario(IReadOnlyList<string> argv, int depth)
    {
        if (depth > MaxShellNesting)
            return false;
        var index = 0;
        while (index < argv.Count)
        {
            var word = argv[index];
            if (ReservedWords.Contains(word) || IsAssignment(word))
            {
                index++;
                continue;
            }
            var wrapper = ProgramName(word);
            if (!Wrappers.Contains(wrapper))
                break;
            index++;
            if (wrapper == "env")
            {
                while (index < argv.Count)
                {
                    var option = argv[index];
                    if (option == "--")
                    {
                        index++;
                        break;
                    }
                    if (option is "-u" or "--unset" or "-C" or "--chdir")
                    {
                        index += 2;
                        continue;
                    }
                    if (option is "-S" or "--split-string")
                    {
                        index++;
                        return index < argv.Count
                            && ExecutesEnvSplitString(argv[index], argv.Skip(index + 1), depth);
                    }
                    if (option.StartsWith("--split-string=", StringComparison.Ordinal))
                        return ExecutesEnvSplitString(option["--split-string=".Length..], argv.Skip(index + 1), depth);
                    if (option.StartsWith("-S", StringComparison.Ordinal) && option.Length > 2)
                        return ExecutesEnvSplitString(option[2..], argv.Skip(index + 1), depth);
                    if (option.StartsWith('-'))
                    {
                        index++;
                        continue;
                    }
                    break;
                }
                continue;
            }
            while (index < argv.Count && argv[index].StartsWith('-'))
            {
                // timeout -s SIGNAL / -k DURATION take a value.
                index += wrapper == "timeout" && argv[index] is "-s" or "-k" ? 2 : 1;
            }
            // timeout DURATION COMMAND...
            if (wrapper == "timeout" && index < argv.Count)
                index++;
        }
        if (index >= argv.Count)
            return false;

        var program = ProgramName(argv[index]);
        var arguments = argv.Skip(index + 1).ToArray();
        if (program == "compose-smoke-test.sh")
            return true;
        if (program == "scenario.sh")
            return TargetsCompose(arguments);
        if (Shells.Contains(program))
            return ShellRunsComposeScenario(arguments, depth);
        return false;
    }

    private static bool ExecutesEnvSplitString(string splitString, IEnumerable<string> remaining, int depth)
    {
        var commands = ShellWords.SimpleCommands(splitString);
        return commands.Count == 1
            && ExecutesComposeScenario(["env", .. commands[0], .. remaining], depth + 1);
    }

    /// <summary>
    /// <c>sh [options] -c SCRIPT [name args]</c> runs SCRIPT; <c>sh [options]
    /// FILE args</c> runs FILE. Combined flags (<c>-lc</c>, <c>-ec</c>) are
    /// what the review plan emits for verify commands.
    /// </summary>
    private static bool ShellRunsComposeScenario(IReadOnlyList<string> arguments, int depth)
    {
        var readsScriptString = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--")
            {
                index++;
                return index < arguments.Count && Operand(arguments, index, readsScriptString, depth);
            }
            if (argument.StartsWith("--", StringComparison.Ordinal))
                continue;
            if (argument.Length > 1 && (argument[0] == '-' || argument[0] == '+'))
            {
                if (argument[0] == '-' && argument.Contains('c'))
                    readsScriptString = true;
                // -o OPTION / -O OPTION take a value.
                if (argument[1..] is "o" or "O")
                    index++;
                continue;
            }
            return Operand(arguments, index, readsScriptString, depth);
        }
        return false;
    }

    private static bool Operand(IReadOnlyList<string> arguments, int index, bool readsScriptString, int depth)
        => readsScriptString
            ? ShellWords.SimpleCommands(arguments[index])
                .Any(words => ExecutesComposeScenario(words, depth + 1))
            : ExecutesComposeScenario(arguments.Skip(index).ToArray(), depth + 1);

    private static bool TargetsCompose(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], "--target=compose", StringComparison.Ordinal))
                return true;
            if (string.Equals(arguments[index], "--target", StringComparison.Ordinal)
                && index + 1 < arguments.Count
                && string.Equals(arguments[index + 1], "compose", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string ProgramName(string word)
    {
        var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        if (equals <= 0 || !(char.IsAsciiLetter(word[0]) || word[0] == '_'))
            return false;
        for (var index = 1; index < equals; index++)
        {
            if (!(char.IsAsciiLetterOrDigit(word[index]) || word[index] == '_'))
                return false;
        }
        return true;
    }
}

/// <summary>
/// AGT-2993: the subset of POSIX shell word splitting the compose disk
/// admission needs to find the program of every simple command in a
/// <c>sh -c</c> script. Quotes and backslashes are removed as the shell does,
/// comments are dropped, <c>; &amp; | &amp;&amp; || ( )</c> and newlines
/// separate commands and redirection targets are skipped. A command
/// substitution (<c>$(...)</c> or backticks, bare or inside double quotes)
/// stays an opaque part of the word that contains it, and its body is parsed
/// into simple commands of its own, because the shell executes it.
/// </summary>
internal static class ShellWords
{
    internal static IReadOnlyList<IReadOnlyList<string>> SimpleCommands(string script)
    {
        var commands = new List<IReadOnlyList<string>>();
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        var inWord = false;
        var skipNextWord = false;
        var nextWordIsHeredocDelimiter = false;
        var heredocDelimiters = new Queue<string>();
        var substitutions = new List<string>();

        void EndWord()
        {
            if (!inWord)
                return;
            if (skipNextWord)
            {
                if (nextWordIsHeredocDelimiter)
                    heredocDelimiters.Enqueue(word.ToString());
                skipNextWord = false;
                nextWordIsHeredocDelimiter = false;
            }
            else
                words.Add(word.ToString());
            word.Clear();
            inWord = false;
        }

        void EndCommand()
        {
            EndWord();
            skipNextWord = false;
            nextWordIsHeredocDelimiter = false;
            if (words.Count > 0)
                commands.Add(words.ToArray());
            words.Clear();
        }

        var index = 0;
        while (index < script.Length)
        {
            var character = script[index];
            switch (character)
            {
                case ' ' or '\t' or '\r':
                    EndWord();
                    index++;
                    break;
                case '\n':
                    EndCommand();
                    index = SkipHeredocBodies(script, index + 1, heredocDelimiters);
                    break;
                case ';' or '&' or '|' or '(' or ')':
                    EndCommand();
                    index++;
                    break;
                case '#' when !inWord:
                    while (index < script.Length && script[index] != '\n')
                        index++;
                    break;
                case '<' or '>':
                    // A pure file-descriptor number before the operator (2>) is not a word.
                    if (inWord && word.Length > 0 && word.ToString().All(char.IsAsciiDigit))
                    {
                        word.Clear();
                        inWord = false;
                    }
                    EndWord();
                    var start = index;
                    while (index < script.Length && script[index] is '<' or '>' or '&' or '|' or '-')
                        index++;
                    skipNextWord = true;
                    nextWordIsHeredocDelimiter = script.AsSpan(start, index - start).StartsWith("<<");
                    break;
                case '\'':
                    inWord = true;
                    index++;
                    while (index < script.Length && script[index] != '\'')
                        word.Append(script[index++]);
                    index++;
                    break;
                case '"':
                    inWord = true;
                    index++;
                    while (index < script.Length && script[index] != '"')
                    {
                        if (script[index] == '$' && index + 1 < script.Length && script[index + 1] == '(')
                        {
                            index = AppendSubstitution(script, index, word, substitutions);
                            continue;
                        }
                        if (script[index] == '`')
                        {
                            index = AppendBackquoted(script, index, word, substitutions);
                            continue;
                        }
                        if (script[index] == '\\' && index + 1 < script.Length
                            && script[index + 1] is '"' or '\\' or '$' or '`' or '\n')
                        {
                            if (script[index + 1] != '\n')
                                word.Append(script[index + 1]);
                            index += 2;
                            continue;
                        }
                        word.Append(script[index++]);
                    }
                    index++;
                    break;
                case '\\':
                    if (index + 1 < script.Length && script[index + 1] != '\n')
                    {
                        word.Append(script[index + 1]);
                        inWord = true;
                    }
                    index += 2;
                    break;
                case '$' when index + 1 < script.Length && script[index + 1] == '(':
                    inWord = true;
                    index = AppendSubstitution(script, index, word, substitutions);
                    break;
                case '`':
                    inWord = true;
                    index = AppendBackquoted(script, index, word, substitutions);
                    break;
                default:
                    inWord = true;
                    word.Append(character);
                    index++;
                    break;
            }
        }
        EndCommand();
        foreach (var body in substitutions)
            commands.AddRange(SimpleCommands(body));
        return commands;
    }

    // A here-document body is data, not commands: skip each body line up to
    // its delimiter line (leading tabs allowed, as with <<-).
    private static int SkipHeredocBodies(string script, int index, Queue<string> delimiters)
    {
        while (delimiters.Count > 0 && index < script.Length)
        {
            var newline = script.IndexOf('\n', index);
            var end = newline < 0 ? script.Length : newline;
            if (script[index..end].TrimStart('\t').TrimEnd('\r') == delimiters.Peek())
                delimiters.Dequeue();
            index = end + 1;
        }
        delimiters.Clear();
        return index;
    }

    // Appends "$( ... )" verbatim, records its body (not that of an arithmetic
    // "$(( ... ))") and returns the index after the closing paren. Quoted and
    // escaped parens do not count towards the nesting. Quotes are skipped only
    // to find the closing paren; SimpleCommands re-parses the recorded body,
    // including substitutions inside double quotes (see the review round 3
    // rows in ComposeScenarioDiskAdmissionTests.Commands).
    private static int AppendSubstitution(
        string script,
        int index,
        System.Text.StringBuilder word,
        List<string> substitutions)
    {
        var start = index;
        var depth = 0;
        var closed = false;
        index++;
        while (index < script.Length)
        {
            var character = script[index];
            if (character == '\\')
            {
                index += 2;
                continue;
            }
            if (character is '\'' or '"')
            {
                index = SkipQuoted(script, index);
                continue;
            }
            index++;
            if (character == '(')
                depth++;
            else if (character == ')' && --depth == 0)
            {
                closed = true;
                break;
            }
        }
        index = Math.Min(index, script.Length);
        word.Append(script, start, index - start);
        var arithmetic = start + 2 < script.Length && script[start + 2] == '(';
        if (!arithmetic)
            substitutions.Add(script[(start + 2)..(closed ? index - 1 : index)]);
        return index;
    }

    // Appends "`...`" verbatim, records its body with the backslash escapes
    // the shell removes before running it, and returns the index after it.
    private static int AppendBackquoted(
        string script,
        int index,
        System.Text.StringBuilder word,
        List<string> substitutions)
    {
        var start = index++;
        var body = new System.Text.StringBuilder();
        while (index < script.Length && script[index] != '`')
        {
            if (script[index] == '\\' && index + 1 < script.Length)
            {
                if (script[index + 1] is not ('$' or '`' or '\\'))
                    body.Append('\\');
                body.Append(script[index + 1]);
                index += 2;
                continue;
            }
            body.Append(script[index++]);
        }
        if (index < script.Length)
            index++;
        word.Append(script, start, index - start);
        substitutions.Add(body.ToString());
        return index;
    }

    // Returns the index after the quoted string that starts at index.
    private static int SkipQuoted(string script, int index)
    {
        var quote = script[index++];
        while (index < script.Length && script[index] != quote)
            index += quote == '"' && script[index] == '\\' ? 2 : 1;
        return Math.Min(index + 1, script.Length);
    }
}

internal sealed record ComposeScenarioDiskDecision(
    bool Admit,
    string Path,
    long? FreeBytes,
    long? TotalBytes,
    double? FreePercent,
    int MinFreePercent)
{
    /// <summary>Journal line written before every compose scenario step.</summary>
    public string Describe(string stepId)
        => $"review-compose-scenario-disk step={stepId} path={Path} " +
           $"freeBytes={FreeBytes?.ToString() ?? "unknown"} totalBytes={TotalBytes?.ToString() ?? "unknown"} " +
           $"freePercent={FreePercent?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
           $"minFreePercent={MinFreePercent} decision={(Admit ? "admit" : "refuse")}";

    /// <summary>Operator-facing reason recorded with the refused review attempt.</summary>
    public string RefusalSummary(string stepId, string commandLine)
        => $"Review command '{stepId}' is a compose scenario and was not started: {Path} has " +
           $"{FreePercent?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} % free " +
           $"({FreeBytes} of {TotalBytes} bytes), below the {MinFreePercent} % floor. " +
           "A compose scenario builds about 2.6 GB of images and build cache; free Docker space " +
           "(scripts/docker-scenario-retention.sh) and the review is retried. Nothing about the " +
           $"reviewed change was evaluated: {commandLine}.";
}
