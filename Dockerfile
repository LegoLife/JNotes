# syntax=docker/dockerfile:1
# Multi-arch: builds natively on the Pi, or cross-builds with
#   docker buildx build --platform linux/arm64 ...

# ---- 1. Editor bundle (Tiptap) ----
FROM --platform=$BUILDPLATFORM node:22-alpine AS editor
WORKDIR /build/Editor
COPY src/JNotes.Web/Editor/package.json src/JNotes.Web/Editor/package-lock.json ./
RUN npm ci
COPY src/JNotes.Web/Editor/ ./
RUN npm run build   # writes /build/wwwroot/js/editor.js

# ---- 2. Claude Code (native binary for the target platform) ----
# Answers inline ✨ requests using the Claude subscription (CLAUDE_CODE_OAUTH_TOKEN from `claude setup-token`).
FROM --platform=$BUILDPLATFORM node:22-alpine AS claude
ARG TARGETARCH
ARG CLAUDE_CODE_VERSION=2.1.318
WORKDIR /cc
RUN arch=$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
 && npm pack "@anthropic-ai/claude-code-linux-${arch}@${CLAUDE_CODE_VERSION}" \
 && tar xzf ./*.tgz && chmod +x package/claude

# ---- 3. .NET publish ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/JNotes.Core/JNotes.Core.csproj src/JNotes.Core/
COPY src/JNotes.Data/JNotes.Data.csproj src/JNotes.Data/
COPY src/JNotes.Web/JNotes.Web.csproj src/JNotes.Web/
RUN dotnet restore src/JNotes.Web/JNotes.Web.csproj -a $TARGETARCH
COPY src/ src/
COPY --from=editor /build/wwwroot/js/ src/JNotes.Web/wwwroot/js/
RUN dotnet publish src/JNotes.Web/JNotes.Web.csproj -c Release -a $TARGETARCH --no-restore -o /app

# ---- 4. Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY --from=claude /cc/package/claude /usr/local/bin/claude
RUN mkdir -p /data && chown $APP_UID /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    JNotes__DataDirectory=/data \
    CLAUDE_CONFIG_DIR=/data/claude \
    DISABLE_AUTOUPDATER=1
VOLUME /data
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "JNotes.Web.dll"]
