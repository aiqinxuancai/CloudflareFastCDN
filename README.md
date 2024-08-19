# CloudflareFastCDN
将CF的IP子网全拿到，然后每个子网中拿一个IP，然后进行多轮ICMPPing来筛选最快速和稳定的IP并通过API更新CF的A记录。

仅支持IPv4，不测IP的下载带宽，在家里的docker上跑了几天，工作良好。

## 如何使用

### 直接运行
编译后执行参数，例子：
```
CloudflareFastCDN --CLOUDFLARE_KEY=你的CFKEY --DOMAINS=cdn.xxx.com,cdn.hahaha.com --PING_THREADS=16 --MAX_IPS=400 --RUN_MINUTES=30
```

### Docker运行
```
docker run -e CLOUDFLARE_KEY=你的CLOUDFLARE_KEY -e DOMAINS=你要更新A记录的域名 -e PING_THREADS=16 -e MAX_IPS=400 -e RUN_MINUTES=30 aiqinxuancai/cloudfarefastcdn:latest
```

### 变量解释
* **CLOUDFLARE_KEY** #必填，请自行获取，必须有你要使用的域名的DNS区域编辑权限。
* **DOMAINS** #必填，使用半角逗号分割，将需要更新A记录的域名写出来，比如cdn.a.com,cdn2.a.com。
* **PING_THREADS** #ping的线程数，默认是16，如果CPU性能很高，可适当调高，**过高可能导致丢包率大幅提升**，我这里16在N100上没问题，在13900HK上可以开到200.
* **MAX_IPS** #最多选取多少个IP来进行测试，会在网段中选取IP后再从中均匀随机获取。
* **RUN_MINUTES** #运行间隔分钟，默认30分钟一次。


## 免责声明

本项目不提供任何互联网服务，仅是一个批量PingIP的命令行工具而已，本工具未特地的提供给任何人，也没有任何侵入计算机系统、修改带有版权的软件等，均为使用者自愿下载使用，任何造成的违法后果与本项目无关。
