# 多阶段构建：编译 → 发布 → 精简运行时镜像
#
# 构建：
#   docker build -f src/DeviceGateway.Server/Dockerfile -t device-gateway:dev .
# 运行：
#   docker run -p 8080:8080 -p 5001:5001 device-gateway:dev

# ---------- 构建阶段 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只拷项目文件做还原，这样改代码时不必重复还原依赖（利用 Docker 层缓存）。
COPY src/DeviceGateway.Shared/DeviceGateway.Shared.csproj src/DeviceGateway.Shared/
COPY src/DeviceGateway.Server/DeviceGateway.Server.csproj src/DeviceGateway.Server/
COPY tests/DeviceGateway.Tests/DeviceGateway.Tests.csproj tests/DeviceGateway.Tests/
COPY DeviceGateway.slnx ./

RUN dotnet restore src/DeviceGateway.Server/DeviceGateway.Server.csproj

# 再拷源码并发布。
COPY . .
RUN dotnet publish src/DeviceGateway.Server/DeviceGateway.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ---------- 运行阶段 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# 以非 root 用户运行。
RUN useradd -m -u 1000 appuser
USER appuser

COPY --from=build /app/publish ./

# 8080 提供 HTTP 接口，5001 是设备接入的 TCP 端口。
EXPOSE 8080
EXPOSE 5001

# 容器健康检查：探测 HTTP 接口是否就绪。
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD ["/bin/sh", "-c", "curl -fsS http://127.0.0.1:8080/api/Health/Check || exit 1"]

ENTRYPOINT ["dotnet", "DeviceGateway.Server.dll"]
