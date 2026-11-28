# Bash sources this file before evaluating its own -c argument.
# Codex's snapshot wrapper execs a fresh Bash for the actual command.
if [[ ${BASH_EXECUTION_STRING-} == *"if . '"*"/shell_snapshots/"*" >/dev/null 2>&1; then :; fi"* &&
      ( ${BASH_EXECUTION_STRING-} == *"exec '/bin/bash' -c '"* ||
        ${BASH_EXECUTION_STRING-} == *"exec '/usr/bin/bash' -c '"* ) ]]; then
    return 0
fi

# Once we reach the tool shell, keep this startup file out of the original
# command's environment and out of scripts or Bash processes it starts.
builtin unset BASH_ENV 2>/dev/null || :
if [[ $- != *i* && -n ${BASH_EXECUTION_STRING-} ]]; then
    # All scratch variables, redirects and command lookup stay in the subshell.
    (
        __studio_status_dir=${BASH_SOURCE[0]%/*}
        __studio_status_notice=$(
            for __studio_status_tool in tail head cat rg grep ls test '[' ps pgrep date wc pwd; do
                case $__studio_status_tool in
                    test|'['|pwd)
                        if [[ $(builtin type -t "$__studio_status_tool") == builtin ]]; then
                            builtin printf '%s builtin\n' "$__studio_status_tool"
                            continue
                        fi
                        ;;
                esac
                # type -p can succeed for a function or alias without printing a path.
                builtin printf '%s ' "$__studio_status_tool"
                builtin type -p "$__studio_status_tool" || :
                builtin printf '\n'
            done | /usr/bin/python3 -I -B "$__studio_status_dir/status_guard.py" \
                "$__studio_status_dir/calls.sqlite3" INTERVAL CAP "$BASH_EXECUTION_STRING" 2>/dev/null
        )
        if [[ -n $__studio_status_notice ]]; then
            builtin printf '%s\n' "$__studio_status_notice" >&2
        fi
    ) || :
fi
