#!/usr/bin/env bash
# Accept an HTTPS origin or an exact loopback HTTP origin before sending secrets.
# Keep the URL an origin so userinfo, paths, queries and fragments cannot alter
# the destination of the API requests built by the caller.
validate_task_server_url() {
    local server="$1"
    if [[ "$server" =~ ^https://([[:alnum:]][[:alnum:].-]*|\[[0-9A-Fa-f:.]+\])(:[0-9]{1,5})?$ ]] ||
       [[ "$server" =~ ^http://(localhost|127\.0\.0\.1|\[::1\])(:[0-9]{1,5})?$ ]]; then
        # -q disables per-user curl settings such as redirects and insecure TLS.
        task_server_curl_options=(-q)
        if [[ "$server" == http://* ]]; then
            task_server_curl_options+=(--noproxy '*')
        fi
        return 0
    fi
    echo "task server URL must be an HTTPS origin or an exact loopback HTTP origin" >&2
    return 2
}
