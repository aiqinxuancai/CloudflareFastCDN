# CloudflareFastCDN

使用 Cloudflare 公布的 IPv4 网段，从每个 `/24` 子网中抽取一个 IP，先进行多轮 ICMP Ping，再对候选 IP 做 HTTP 连通性验证，最终自动通过 Cloudflare API 更新指定域名的 A 记录。

目前仅支持 IPv4。

## 工作流程

1. 从 Cloudflare IPv4 网段中抽样出待测 IP。
2. 第 1 轮对全部候选做 4 次 ICMP Ping。
3. 第 2 轮对前 100 个结果做 10 次 ICMP Ping，进一步筛掉丢包较高的 IP。
4. **子网缓存阶段**：从历史优选中积累的最多 10 个 `/24` 子网里各随机抽取 1 个 IP（共最多 10 个），进行 TCP Ping 连通性验证，通过的直接加入最终候选列表。子网缓存按 HTTP 延迟升序排序，持久化在 `subnet_cache.json`（Docker 下为 `/data/subnet_cache.json`）。
5. 对候选 IP 做 HTTP 验证。
6. 根据 `BANDWIDTH_PRIORITY` 决定最终挑选方式：
   - `false`：只按 HTTP 延迟最小选择，保持旧逻辑。
   - `true`：在 HTTP 验证通过后，尝试访问 `HTTP_PROBE_URL` 同域名下的 `/speedtest` 文件并做下载测速，优先选择带宽最高的 IP。
7. 如果 `BANDWIDTH_PRIORITY=true` 时 `/speedtest` 不存在或全部测速失败，则自动回退到旧的 HTTP 延迟逻辑。
8. 选出最优 IP 后，将其所在 `/24` 子网存入子网缓存（若 CF 对应 CIDR 覆盖完整 `/24`），供下次运行时优先验证。

## 使用方式

### 直接运行

编译后可通过命令行参数启动：

```bash
CloudflareFastCDN \
  --CLOUDFLARE_KEY=你的CFToken \
  --DOMAINS=cdn.example.com,cdn2.example.com \
  --PING_THREADS=8 \
  --MAX_IPS=400 \
  --PING_INTERVAL_MS=150 \
  --HTTP_PROBE_URL=https://www.visa.cn/ \
  --HTTP_PROBE_TIMEOUT_MS=4000 \
  --HTTP_SPEEDTEST_TIMEOUT_MS=10000 \
  --HTTP_SPEEDTEST_IDLE_TIMEOUT_MS=3000 \
  --RUN_MINUTES=30 \
  --BANDWIDTH_PRIORITY=false \
  --UPDATE_IP_LIST=false
```

### Docker 运行

```bash
docker run -d \
  --name cloudflare-fast-cdn \
  --restart unless-stopped \
  -e CLOUDFLARE_KEY=你的CLOUDFLARE_KEY \
  -e DOMAINS=cdn.example.com,cdn2.example.com \
  -e PING_THREADS=8 \
  -e MAX_IPS=400 \
  -e PING_INTERVAL_MS=150 \
  -e HTTP_PROBE_URL=https://www.visa.cn/ \
  -e HTTP_PROBE_TIMEOUT_MS=4000 \
  -e HTTP_SPEEDTEST_TIMEOUT_MS=10000 \
  -e HTTP_SPEEDTEST_IDLE_TIMEOUT_MS=3000 \
  -e RUN_MINUTES=30 \
  -e BANDWIDTH_PRIORITY=false \
  -e UPDATE_IP_LIST=false \
  -v cloudflare-fast-cdn-data:/data \
  aiqinxuancai/cloudfarefastcdn:latest
```

### Docker Compose 运行

创建 `docker-compose.yml`：

```yaml
version: '3.8'

services:
  cloudflare-fast-cdn:
    image: aiqinxuancai/cloudfarefastcdn:latest
    container_name: cloudflare-fast-cdn
    restart: unless-stopped
    environment:
      CLOUDFLARE_KEY: "你的CLOUDFLARE_KEY"
      DOMAINS: "cdn.example.com,cdn2.example.com"
      PING_THREADS: "8"
      MAX_IPS: "400"
      PING_INTERVAL_MS: "150"
      HTTP_PROBE_URL: "https://www.visa.cn/"
      HTTP_PROBE_TIMEOUT_MS: "4000"
      HTTP_SPEEDTEST_TIMEOUT_MS: "10000"
      HTTP_SPEEDTEST_IDLE_TIMEOUT_MS: "3000"
      RUN_MINUTES: "30"
      BANDWIDTH_PRIORITY: "false"
      UPDATE_IP_LIST: "false"
    volumes:
      - cloudflare-fast-cdn-data:/data

volumes:
  cloudflare-fast-cdn-data:
```

启动：

```bash
docker compose up -d
```

查看日志：

```bash
docker compose logs -f cloudflare-fast-cdn
```

停止：

```bash
docker compose down
```

## 参数说明

`CLOUDFLARE_KEY` 和 `DOMAINS` 为必填参数。

| 参数 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `CLOUDFLARE_KEY` | 是 | 无 | Cloudflare API Token，需要具备目标域名所在 Zone 的 DNS 编辑权限。 |
| `DOMAINS` | 是 | 无 | 需要更新 A 记录的域名，多个域名用英文逗号分隔，例如 `cdn.example.com,cdn2.example.com`。 |
| `PING_THREADS` | 否 | `8` | Ping 并发线程数。数值越大检测越快，但过高可能导致丢包率上升。 |
| `MAX_IPS` | 否 | `400` | 每轮最多抽样检测的 IP 数量。程序会先按网段抽取，再从其中随机采样。 |
| `PING_INTERVAL_MS` | 否 | `150` | 单次 Ping 的间隔时间，单位毫秒。 |
| `HTTP_PROBE_URL` | 否 | `https://www.visa.cn/` | HTTP 验证阶段访问的测试地址。建议使用你自己的站点作为验证地址。 请开启黄云！|
| `HTTP_PROBE_TIMEOUT_MS` | 否 | `4000` | 单次 HTTP 连通性验证超时时间，单位毫秒，覆盖连接/TLS/响应头阶段。 |
| `HTTP_SPEEDTEST_TIMEOUT_MS` | 否 | `10000` | 单个 IP 的 `/speedtest` 下载测速总超时时间，单位毫秒。 |
| `HTTP_SPEEDTEST_IDLE_TIMEOUT_MS` | 否 | `3000` | `/speedtest` 下载过程中单次读取的空闲超时，单位毫秒；用于避免服务端只返回响应头后长期不继续下发数据。 |
| `RUN_MINUTES` | 否 | `30` | 每轮任务执行完成后的等待分钟数，随后进入下一轮检测。 |
| `BANDWIDTH_PRIORITY` | 否 | `false` | 是否启用带宽优选。`false` 表示只按 HTTP 延迟最小选择；`true` 表示先做 HTTP 验证，再尝试下载同域 `/speedtest` 做测速，按带宽最高选择。 |
| `UPDATE_IP_LIST` | 否 | `false` | 启动时是否先更新 Cloudflare 官方 IPv4 网段列表，可选值 `true` / `false`。 |

## 参数来源说明

- Docker 环境下，程序从环境变量读取参数。
- 非 Docker 环境下，优先读取环境变量；当 `CLOUDFLARE_KEY` 未提供时，会继续尝试读取命令行参数。
- 命令行参数格式示例：`--DOMAINS=cdn.example.com,cdn2.example.com`

## `/speedtest` 使用说明

- 仅当 `BANDWIDTH_PRIORITY=true` 时，程序才会尝试访问 `/speedtest`。
- `/speedtest` 的实际地址是 `HTTP_PROBE_URL` 所在域名下的 `/speedtest`，例如 `https://www.visa.cn/speedtest`。
- 如果 `/speedtest` 不存在或测速失败，程序会自动回退到按 HTTP 延迟优选。
- 默认情况下，单次 HTTP 探测超时为 `4s`，单个 IP 的测速总超时为 `10s`，下载空闲超时为 `3s`；如网络较差可按需调大。
- 建议准备一个静态测速文件，文件大小至少 `4 MB`，高速链路下结果会更稳定。

## 免责声明

本项目本质上是一个批量检测 IP 连通性并自动更新 DNS 记录的工具，不提供网络攻击、入侵或绕过权限控制等能力。请仅在你拥有合法管理权限的 Cloudflare 账号和域名范围内使用。因使用本项目产生的任何后果，由使用者自行承担。
