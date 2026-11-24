FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
    gcc libc6-dev make gcc-mingw-w64-x86-64 binutils-mingw-w64-x86-64 nasm pkg-config \
    curl python3 python3-venv xz-utils bzip2 unzip zip ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN python3 -m venv /opt/builder-python \
    && /opt/builder-python/bin/pip install --no-cache-dir ziglang==0.15.2
ENV PATH="/opt/builder-python/bin:${PATH}" DOTNET_CLI_TELEMETRY_OPTOUT=1
