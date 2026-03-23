# CloudflareFastCDN

使用 Cloudflare 公布的 IPv4 网段，从每个 `/24` 子网中抽取一个 IP，经过多轮 ICMP Ping 和 HTTP 验证后，自动把最优 IP 更新到 Cloudflare DNS A 记录。

目前仅支持 IPv4。

## 工作流程

1. 从 Cloudflare IPv4 网段中抽样出待测 IP。
2. 第 1 轮对全部候选做 4 次 ICMP Ping。
3. 第 2 轮对前 100 个结果做 10 次 ICMP Ping，进一步筛掉丢包较高的 IP。
4. 对前 10 个候选做 HTTP 验证。
5. 根据 `SELECTION_PRIORITY` 决定最终挑选方式：
   - `latency`：只按 HTTP 延迟最小选择，保持旧逻辑。
   - `bandwidth`：在 HTTP 验证通过后，尝试访问 `HTTP_PROBE_URL` 同域名下的 `/speedtest` 文件并做下载测速，优先选择带宽最高的 IP。
6. 如果 `bandwidth` 模式下 `/speedtest` 不存在或全部测速失败，则自动回退到旧的 HTTP 延迟逻辑。

## 运行方式

### 直接运行

编译后执行：

```bash
CloudflareFastCDN \
  --CLOUDFLARE_KEY=你的CFKEY \
  --DOMAINS=cdn.example.com,cdn2.example.com \
  --PING_THREADS=16 \
  --MAX_IPS=400 \
  --PING_INTERVAL_MS=150 \
  --HTTP_PROBE_URL=https://www.visa.cn/ \
  --RUN_MINUTES=30 \
  --SELECTION_PRIORITY=latency \
  --UPDATE_IP_LIST=false
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
  -e SELECTION_PRIORITY=latency \
  -e UPDATE_IP_LIST=false \
  aiqinxuancai/cloudfarefastcdn:latest
```

### Docker Compose

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
      SELECTION_PRIORITY: "latency"
      UPDATE_IP_LIST: "false"
```

启动服务：

```bash
docker-compose up -d
```

查看日志：

```bash
docker-compose logs -f cloudflare-fast-cdn
```

停止服务：

```bash
docker-compose down
```

## 环境变量说明

- `CLOUDFLARE_KEY`
  Cloudflare API Token。必须具备目标域名所在 Zone 的 DNS 编辑权限。

- `DOMAINS`
  需要自动更新 A 记录的域名，多个域名用英文逗号分隔。

- `PING_THREADS`
  Ping 并发数，默认 `16`。

- `MAX_IPS`
  每轮最多抽样检测多少个 IP，默认 `400`。

- `PING_INTERVAL_MS`
  Ping 间隔，默认 `150` 毫秒。

- `HTTP_PROBE_URL`
  HTTP 验证使用的目标 URL，默认 `https://www.visa.cn/`。
  在 `bandwidth` 模式下，程序会自动尝试访问该 URL 所在域名下的 `/speedtest`，例如 `https://www.visa.cn/speedtest`。

- `RUN_MINUTES`
  每轮运行间隔分钟数，默认 `30`。

- `SELECTION_PRIORITY`
  最终优选策略，默认 `latency`。
  可选值：
  - `latency`
    连接速度优先，只按 HTTP 延迟最小选择，不访问 `/speedtest`。
  - `bandwidth`
    带宽优先，先做 HTTP 验证，再尝试下载 `/speedtest` 进行测速，按带宽最高选择。
    如果 `/speedtest` 不存在或测速失败，会自动回退到 `latency` 逻辑。

- `UPDATE_IP_LIST`
  启动时是否更新 Cloudflare 官方 IPv4 列表，默认 `false`。

## `/speedtest` 使用建议

- 建议准备一个静态测速文件，路径固定为 `/speedtest`。
- 建议文件大小至少 `4 MB` 以上，这样在高速链路下结果更稳定。
- 如果没有准备 `/speedtest` 文件，也不影响使用，程序会自动退回 HTTP 延迟优选。

## 免责声明

本项目本质上是一个批量探测 Cloudflare 公网 IP 的命令行工具，不提供任何网络攻击能力，也不会主动提供互联网服务。请使用者自行判断使用场景及风险，项目作者不对使用本项目造成的任何后果负责。
