FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

COPY ["CloudflareFastCDN.csproj", "./"]
RUN dotnet restore "CloudflareFastCDN.csproj"

COPY . .
RUN dotnet publish "CloudflareFastCDN.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime

WORKDIR /app

COPY --from=build /app/publish .

ARG CLOUDFLARE_KEY=""
ARG DOMAINS=""
ARG PING_THREADS="8"
ARG MAX_IPS="400"
ARG PING_INTERVAL_MS="150"
ARG HTTP_PROBE_URL="https://www.visa.cn/"
ARG RUN_MINUTES="30"
ARG BANDWIDTH_PRIORITY="false"
ARG UPDATE_IP_LIST="false"

ENV CLOUDFLARE_KEY=${CLOUDFLARE_KEY} \
    DOMAINS=${DOMAINS} \
    PING_THREADS=${PING_THREADS} \
    MAX_IPS=${MAX_IPS} \
    PING_INTERVAL_MS=${PING_INTERVAL_MS} \
    HTTP_PROBE_URL=${HTTP_PROBE_URL} \
    RUN_MINUTES=${RUN_MINUTES} \
    BANDWIDTH_PRIORITY=${BANDWIDTH_PRIORITY} \
    UPDATE_IP_LIST=${UPDATE_IP_LIST}

ENTRYPOINT ["dotnet", "CloudflareFastCDN.dll"]
