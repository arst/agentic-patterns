# Teaching sandbox image for the CodeAct sample.
#
# The image itself is deliberately boring: the pinned .NET SDK, nothing else. The
# security boundary is NOT baked in here — it is enforced at `docker run` time by
# ContainerCodeRunner.BuildRunArguments (no network, read-only rootfs, dropped
# capabilities, non-root user, pids/memory/cpu limits, tmpfs-only writes, and a
# read-only bind mount of a single disposable per-run directory).
#
# Keeping the image repo-controlled matters anyway: the host decides exactly which
# SDK compiles and runs the model's code, and the model cannot pick its own image,
# package feeds, or build targets. Pin a digest here for stronger supply-chain
# guarantees in anything beyond a teaching setup.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# The container runs as uid 65532 with HOME=/tmp (a bounded tmpfs); keep the SDK
# from writing anywhere else or phoning home.
ENV DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    NUGET_PACKAGES=/warmup/nuget

# Pre-warm at BUILD time — the only moment network access is legitimate. File-based
# apps implicitly restore the runtime packs (ILCompiler, ILLink, runtime packages),
# so compile one throwaway script now and bake the resulting package cache into the
# image, world-readable. At RUN time the container has no network and an explicitly
# empty package-source list; restore resolves everything from this offline cache.
RUN mkdir -p /warmup \
    && echo 'Console.WriteLine("warmup");' > /warmup/script.cs \
    && dotnet run /warmup/script.cs \
    && chmod -R a+rX /warmup
