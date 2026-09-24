-- 0008_smtp_settings_cleanup —— 邮件字段集重写之后，设置表里留下的死键。
--
-- 背景：邮件配置从「安全方式三选一 + 用户名 + Secret 名 + 发件人名称 + 超时」收窄成 ani-rss 那七项
-- （SMTP 地址、端口、发件人邮箱、密码、SSL、STARTTLS、收件人邮箱）之后，下面这些键不再有任何读取者。
-- 留着它们不是无害的：真实事故是 `smtp.fromName` 里躺着一个 `????` —— 早期的配置脚本把中文当成 ANSI 发出去，
-- 编不出来的字符全变成问号 —— 而通知管理页已经不再提供「发件人名称」输入框，谁也改不掉它，于是收件人看到的
-- 发件人就成了「????」。代码已经不再读这个键；这里把它连同其它死键一起删掉，免得下一个人再被它骗一次。
--
-- 刻意**不删** `smtp.security` / `smtp.useStartTls`：它们参与「老键仍被尊重」的兼容读取，删掉会让回滚到旧镜像
-- 的实例丢掉加密设置。新键（smtp.ssl / smtp.starttls）优先，所以这两行留着也不会改变任何行为。

DELETE FROM app_setting
 WHERE setting_key IN ('smtp.fromName', 'smtp.username', 'smtp.secretName', 'smtp.timeoutSeconds');
