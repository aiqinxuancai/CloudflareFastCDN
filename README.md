# CloudflareFastCDN

使用 Cloudflare 公布的 IPv4 网段，从每个 `/24` 子网中抽取 IP，先进行多轮 ICMP Ping，再对候选 IP 做 HTTP 连通性验证，最终自动通过 Cloudflare API 更新指定域名的 A 记录。

目前仅支持 IPv4。

## 使用方式

### 直接运行

编译后可通过命令行参数启动：

```bash
CloudflareFastCDN --CLOUDFLARE_KEY=你的CFToken --DOMAINS=cdn.example.com,cdn2.example.com --PING_THREADS=16 --MAX_IPS=400 --PING_INTERVAL_MS=150 --HTTP_PROBE_URL=https://www.visa.cn/ --RUN_MINUTES=30 --UPDATE_IP_LIST=false
```

### Docker 运行

```bash
docker run -d \
  --name cloudflare-fast-cdn \
  --restart unless-stopped \
  -e CLOUDFLARE_KEY=你的CLOUDFLARE_KEY \
  -e DOMAINS=cdn.example.com,cdn2.example.com \
  -e PING_THREADS=16 \
  -e MAX_IPS=400 \
  -e PING_INTERVAL_MS=150 \
  -e HTTP_PROBE_URL=https://www.visa.cn/ \
  -e RUN_MINUTES=30 \
  -e UPDATE_IP_LIST=false \
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
      PING_THREADS: "16"
      MAX_IPS: "400"
      PING_INTERVAL_MS: "150"
      HTTP_PROBE_URL: "https://www.visa.cn/"
      RUN_MINUTES: "30"
      UPDATE_IP_LIST: "false"
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

> `CLOUDFLARE_KEY` 和 `DOMAINS` 为必填参数。

| 参数 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- |
| **`CLOUDFLARE_KEY`** | **是** | 无 | Cloudflare API Token，需要具备目标域名对应 Zone 的 DNS 编辑权限。 |
| **`DOMAINS`** | **是** | 无 | 需要更新 A 记录的域名，多个域名用英文逗号分隔，例如 `cdn.example.com,cdn2.example.com`。 |
| `PING_THREADS` | 否 | `16` | Ping 并发线程数。数值越大检测越快，但过高可能导致丢包率上升。 |
| `MAX_IPS` | 否 | `400` | 本轮最多抽样检测的 IP 数量。程序会先按网段抽取，再从中随机采样。 |
| `PING_INTERVAL_MS` | 否 | `150` | 单次 Ping 的间隔时间，单位毫秒。 |
| `HTTP_PROBE_URL` | 否 | `https://www.visa.cn/` | HTTP 验证阶段访问的测试地址。建议使用稳定、可正常访问的 HTTPS 地址。 |
| `RUN_MINUTES` | 否 | `30` | 每轮任务执行完成后的等待分钟数，随后进入下一轮检测。 |
| `UPDATE_IP_LIST` | 否 | `false` | 启动时是否先更新 Cloudflare 官方 IPv4 网段列表。可选值：`true` / `false`。 |

## 参数来源说明

- Docker 环境下，程序从环境变量读取参数。
- 非 Docker 环境下，优先读取环境变量；当 `CLOUDFLARE_KEY` 未提供时，会继续尝试读取命令行参数。
- 命令行参数格式示例：`--DOMAINS=cdn.example.com,cdn2.example.com`

## 免责声明

本项目本质上是一个批量检测 IP 连通性并自动更新 DNS 记录的工具，不提供网络攻击、入侵或绕过权限控制等能力。请仅在你拥有合法管理权限的 Cloudflare 账号和域名范围内使用。因使用本项目产生的任何后果，由使用者自行承担。
