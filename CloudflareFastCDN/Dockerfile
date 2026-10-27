FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

COPY ["CloudflareFastCDN.csproj", "./"]
RUN dotnet restore "CloudflareFastCDN.csproj"

COPY . .
RUN dotnet publish "CloudflareFastCDN.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .
COPY docker-entrypoint.sh .

RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata \
    && chmod +x /app/docker-entrypoint.sh \
    && mkdir -p /data \
    && rm -rf /var/lib/apt/lists/*

# 运行设置通过 Web 管理页面配置并保存到 /data/settings.json；
# 在此处或 docker run / compose 中设置的环境变量会锁定对应项目，因此镜像不再内置默认值
ARG TZ="Etc/UTC"
ENV TZ=${TZ}

EXPOSE 19401

ENTRYPOINT ["./docker-entrypoint.sh"]

VOLUME ["/data"]
