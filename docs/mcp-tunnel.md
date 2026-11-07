# Connect claude.ai and ChatGPT to a self-hosted Nostos

claude.ai and ChatGPT connect to MCP servers from their own cloud. A Nostos on
your home network is not reachable from there, so these apps need two things:

1. **A public HTTPS address** that forwards a few paths to Nostos: a tunnel
   (Tailscale Funnel, Cloudflare Tunnel) or a reverse proxy.
2. **`Mcp__PublicBaseUrl`** set to that address, which turns on Nostos's own
   small OAuth server. The app signs in through it, and you approve each app
   in Nostos.

Local apps (Claude Code, Cursor, VS Code, Claude Desktop) do not need any of
this. They use an access token from **Settings → Connected apps**.

## What becomes public

Only these paths:

| Path | What it is |
| --- | --- |
| `/mcp` | The MCP endpoint. Every request needs a token. |
| `/oauth/authorize`, `/oauth/authorize/status` | The page that asks you to approve the app, and the status it polls |
| `/oauth/token`, `/oauth/revoke` | Where the app gets, refreshes and gives back its tokens |
| `/.well-known/oauth-protected-resource`, `/.well-known/oauth-protected-resource/mcp`, `/.well-known/oauth-authorization-server` | OAuth metadata |

Nothing else: not the app, not `/api`, not `/opds`, not the approve buttons.
Set up the tunnel to forward only these paths (the examples below do). Nostos
also checks every request itself: a request that arrives on the public address
(or through Tailscale Funnel) and asks for any other path gets `404`. That
check is the backstop if the tunnel forwards more than it should; do not rely
on it alone.

**Approving happens only on your own network.** The public page never logs
anyone in. It shows a four-letter code and asks you to open Nostos at home and
approve the request in **Settings → Connected apps**. Approve it only if you
started it and the code matches.

## 1. Set the public address

Pick the address first (the steps below show where it comes from), then set it
and restart Nostos:

```yaml
# compose.yml
services:
  nostos:
    environment:
      - Mcp__PublicBaseUrl=https://nostos.example.ts.net
```

The value must be `https://` with no path. Nostos logs
`MCP OAuth enabled for hosted AI apps at https://…/mcp` at startup when it took
it, and a warning when it did not.

The connector address for your AI app is `{Mcp__PublicBaseUrl}/mcp`, for
example `https://nostos.example.ts.net/mcp`. Settings → Connected apps shows it
with a copy button.

**Keep using your private address for the app itself.** On the public address
Nostos answers only the paths above, so open the app as before (LAN address,
Tailscale IP, or a different port).

Links in what Nostos gives the app (to a book, a note, To read) cannot use the
public address. To make them clickable, also set the address you open Nostos
at; without it they are paths such as `/library/…`:

```yaml
      - Mcp__AppBaseUrl=https://nostos.example.ts.net:8443
```

## 2. Make the address public

The examples assume Nostos listens on `127.0.0.1:5099` (the port in
`compose.yml`). Use your own port.

### Tailscale Funnel

Funnel publishes a port of your Tailscale machine at
`https://<machine>.<tailnet>.ts.net`. Turn on HTTPS certificates and Funnel for
the tailnet first ([Tailscale's Funnel guide](https://tailscale.com/kb/1223/funnel)).
Then forward each path, and only those:

```bash
tailscale funnel --bg --set-path /mcp http://127.0.0.1:5099/mcp
tailscale funnel --bg --set-path /oauth http://127.0.0.1:5099/oauth
tailscale funnel --bg --set-path /.well-known/oauth-protected-resource http://127.0.0.1:5099/.well-known/oauth-protected-resource
tailscale funnel --bg --set-path /.well-known/oauth-authorization-server http://127.0.0.1:5099/.well-known/oauth-authorization-server
tailscale funnel status
```

Set `Mcp__PublicBaseUrl=https://<machine>.<tailnet>.ts.net`.

- Funnel makes **everything** served on that port public. If you already use
  `tailscale serve` to open Nostos on port 443 inside your tailnet, move that
  to another port first (`tailscale serve --bg --https 8443 http://127.0.0.1:5099`)
  and open the app at `https://<machine>.<tailnet>.ts.net:8443`.
- Tailscale marks every Funnel request (`Tailscale-Funnel-Request`), and Nostos
  answers only the allowed paths for those, even if a Funnel forwards `/`.
- Use port 443. Funnel also offers 8443 and 10000, but in a test in November
  2026 claude.ai's connector check could not reach a Funnel on port 10000
  (no request arrived), while the same address answered from elsewhere. If
  443 already serves something else, move that instead.
- To stop: `tailscale funnel --https=443 off` (or the port you used).
  `tailscale funnel reset` also removes every other serve and Funnel setting
  on the machine.

### Cloudflare Tunnel

Route a hostname of your Cloudflare zone through `cloudflared`, with one
ingress rule per path and `404` for the rest:

```yaml
# ~/.cloudflared/config.yml
tunnel: <tunnel-id>
credentials-file: /home/<you>/.cloudflared/<tunnel-id>.json
ingress:
  - hostname: nostos.example.com
    path: ^/mcp$
    service: http://127.0.0.1:5099
  - hostname: nostos.example.com
    path: ^/oauth/(authorize|authorize/status|token|revoke)$
    service: http://127.0.0.1:5099
  - hostname: nostos.example.com
    path: ^/\.well-known/oauth-(protected-resource(/mcp)?|authorization-server)$
    service: http://127.0.0.1:5099
  - service: http_status:404
```

```bash
cloudflared tunnel route dns <tunnel-id> nostos.example.com
cloudflared tunnel run <tunnel-id>
```

Set `Mcp__PublicBaseUrl=https://nostos.example.com`. Do not put Cloudflare
Access in front of these paths: claude.ai and ChatGPT cannot pass it.

### A reverse proxy (Caddy)

On a machine with a public IP and a domain name, Caddy gets the certificate
and forwards only the allowed paths:

```caddyfile
nostos.example.com {
	@nostos path /mcp /oauth/authorize /oauth/authorize/status /oauth/token /oauth/revoke /.well-known/oauth-protected-resource /.well-known/oauth-protected-resource/mcp /.well-known/oauth-authorization-server
	handle @nostos {
		reverse_proxy 127.0.0.1:5099
	}
	handle {
		respond 404
	}
}
```

Set `Mcp__PublicBaseUrl=https://nostos.example.com`. nginx and Traefik work the
same way: forward the paths above, keep the `Host` header, answer `404` for
everything else.

### Check it

From a machine outside your network (or a phone off Wi-Fi):

```bash
curl -s https://nostos.example.com/.well-known/oauth-protected-resource/mcp   # JSON, "resource": ".../mcp"
curl -si https://nostos.example.com/mcp -X POST | head -n 1                    # 401
curl -si https://nostos.example.com/ | head -n 1                               # 404
curl -si https://nostos.example.com/api/books | head -n 1                      # 404
```

## 3. Add the connector

### claude.ai

1. In claude.ai, open **Settings → Connectors** and choose **Add custom
   connector** (on plans that allow custom connectors).
2. Name it `Nostos` and enter the connector address, `https://…/mcp`. Leave the
   OAuth client fields empty.
3. Choose **Connect**. A window opens with "Claude wants to connect to your
   Nostos" and a four-letter code.
4. On your own network, open Nostos, go to **Settings → Connected apps**, and
   find the request with the same code. Choose **Read only** or **Read and
   write**, then **Approve**.
5. The window closes by itself. Claude appears under Connected apps.

### ChatGPT

1. In ChatGPT, open **Settings → Apps & Connectors → Advanced settings** and
   turn on **Developer mode** (on plans that allow it).
2. Choose **Create**, name it `Nostos`, enter the connector address, and pick
   **OAuth** as the authentication.
3. Continue with steps 3 to 5 above. The code page names the app as ChatGPT
   calls itself.

## Revoke

**Settings → Connected apps** lists each approved app. Revoking one stops its
next request and its next token refresh at once; the app must be approved
again to come back. Notes and drafts it already saved stay in your library.

## Security notes

- Access tokens last one hour. Refresh tokens are used once and replaced; if
  one is used again more than 30 seconds later, Nostos revokes the whole
  connection. Within 30 seconds it is a retry or two refreshes at once, and the
  app gets a fresh pair.
- An app idle for 60 days drops off the list; connect it again to use it.
- Turning OAuth off (removing `Mcp__PublicBaseUrl`) stops every approved app at
  once. Access tokens you made in Settings keep working.
- Nostos stores only SHA-256 fingerprints of tokens.
- Apps identify themselves with a client metadata document (an https URL).
  Nostos fetches it with tight limits: https only, public addresses only
  (checked again after DNS and after each redirect), 5 seconds, 16 KiB. There is
  no dynamic client registration. An app that offers only dynamic registration
  cannot connect.
- The approve, deny and list routes answer `404` on the public address.
  Approve and deny also take only a JSON request from Settings itself, so
  another website cannot approve a request in your browser. When a proxy
  forwards the request, the caller must be on a private, loopback or Tailscale
  address, even if the proxy rewrites the host.
- Requests waiting for approval end after 10 minutes. At most 20 wait at once,
  and at most 3 from one address.
- The public OAuth endpoints are rate-limited per address. Behind Docker's port
  mapping every request may come from the same bridge address; the limits then
  apply to all callers together.

## If it does not connect

- **The app says it cannot find the authorization server.** Check that the
  `/.well-known` paths are forwarded and that `Mcp__PublicBaseUrl` is exactly
  the address you entered, without `/mcp`.
- **The code page says the app cannot connect.** The app's client metadata
  document could not be read or did not check out; the page says why.
- **The code page waits forever.** The request is approved in Nostos on your
  own network, not on the public address. Requests end after 10 minutes; start
  again from the app.
- **The app worked, then stopped.** It was revoked in Settings, or it was idle
  for 60 days. Connect it again.
