# CloudflareFastCDN
用于自动使用ping优选状态较好的Cloudflare节点IP，并更新到指定CF的A记录中。

## 如何使用

### 直接运行
编译后执行参数，例子：
```
CloudflareFastCDN --CLOUDFLARE_KEY=你的CFKEY --DOMAINS=cdn.xxx.com,cdn.hahaha.com --PING_THREADS=16 --MAX_IPS=400
```

### Docker运行
```
docker run -e CLOUDFLARE_KEY=你的CLOUDFLARE_KEY -e DOMAINS=你要更新A记录的域名 -e PING_THREADS=16 -e MAX_IPS=400 aiqinxuancai/cloudfarefastcdn:latest
```
* 环境变量
```
CLOUDFLARE_KEY
DOMAINS
PING_THREADS
MAX_IPS
```
