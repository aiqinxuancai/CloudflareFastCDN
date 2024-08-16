# 使用官方的 .NET SDK 镜像作为构建镜像
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# 设置工作目录
WORKDIR /app

# 复制 csproj 并还原依赖项
COPY *.csproj ./
RUN dotnet restore

# 复制其余的项目文件
COPY . ./

# 构建项目
RUN dotnet publish -c Release -o out

# 使用官方的 .NET 运行时镜像作为基础镜像
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

# 设置工作目录
WORKDIR /app

# 复制已发布的文件到运行时镜像
COPY --from=build /app/out .

# 声明构建参数
ARG CLOUDFLARE_KEY
ARG DOMAINS

# 使用构建参数设置环境变量
ENV CLOUDFLARE_KEY=${CLOUDFLARE_KEY} \
    DOMAINS=${DOMAINS}

# 将 /cert 目录挂载到容器外部
VOLUME ["/cert"]

# 设置启动命令
ENTRYPOINT ["dotnet", "CloudflareFastCDN.dll"]