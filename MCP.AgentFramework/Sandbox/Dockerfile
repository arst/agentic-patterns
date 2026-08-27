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
#
# The BASE image is pinned by digest too, not just by tag: `node:22-alpine` is mutable, so
# two people building this image on different days would otherwise get different bases while
# the docs claim a pinned, reproducible server. The tag is kept alongside the digest purely as
# documentation - docker verifies the digest. Re-resolve it deliberately when bumping:
#   docker buildx imagetools inspect node:22-alpine   # -> Digest: sha256:...
# (that is the multi-arch INDEX digest, so the pin still works on arm64 and amd64).
FROM node:22-alpine@sha256:c610fcdfb1d5b4740dd70c284ed3cb16bb857e0f7166196e36a5501df7a3aa32
ARG SERVER_VERSION=2025.8.18
RUN npm install -g @modelcontextprotocol/server-everything@${SERVER_VERSION} \
 && addgroup -S mcp && adduser -S -G mcp mcp
USER mcp
ENTRYPOINT ["mcp-server-everything"]
