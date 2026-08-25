# Sandbox image for the third-party MCP demo server.
#
# The upstream sample spawns `npx -y @modelcontextprotocol/server-everything`, which
# downloads an UNPINNED package at run time and executes it on the host with the
# application's environment. This image replaces that: the exact server version is
# baked in at BUILD time (the only moment network access is legitimate), so the
# sandbox can run with `--network none` and no host credentials at all. The security
# boundary itself is enforced at `docker run` time by
# Shared.Sandbox.SandboxRunner.BuildRunArguments (no network, read-only rootfs,
# dropped capabilities, non-root user, pids/memory/cpu limits).
FROM node:22-alpine
ARG SERVER_VERSION=2025.8.18
RUN npm install -g @modelcontextprotocol/server-everything@${SERVER_VERSION} \
 && addgroup -S mcp && adduser -S -G mcp mcp
USER mcp
ENTRYPOINT ["mcp-server-everything"]
