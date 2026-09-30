// 复制到剪贴板：先试现代 API，失败再走旧的 execCommand。
//
// 为什么需要两条路：navigator.clipboard 只在**安全上下文**（HTTPS 或 localhost）里存在，而这个实例
// 默认是明文 HTTP（局域网、VPN 里都是），此时 navigator.clipboard 是 undefined。
//
// 但「明文 HTTP 下没法自动复制」并不成立 —— document.execCommand('copy') 不受安全上下文限制，
// 在同一个明文页面上确实能把内容放进剪贴板（已用「复制后真的粘贴回来」验证过）。所以这不是浏览器
// 做不到，只是需要一条兜底；禁用它也不带来安全收益：明文 HTTP 下这串码本来就在网络上裸传。
//
// 返回 { ok, secure, how }：
//   ok      内容是否确实进了剪贴板
//   secure  当前是不是安全上下文 —— 调用方据此决定要不要提醒「这次传输是明文的」
//   how     走的是哪条路（'clipboard' / 'execCommand'），排查时有用
window.dmCopy = async (text) => {
    const secure = window.isSecureContext === true;

    if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
        try {
            await navigator.clipboard.writeText(text);
            return { ok: true, secure: secure, how: 'clipboard' };
        } catch {
            // 权限被拒、页面失焦、浏览器策略：落到下面的兜底，而不是立刻报失败。
        }
    }

    try {
        const area = document.createElement('textarea');
        area.value = text;
        area.setAttribute('readonly', '');
        // 不能 display:none 或 visibility:hidden —— 那样选不中，execCommand 会返回 false。
        area.style.position = 'fixed';
        area.style.top = '0';
        area.style.left = '-9999px';
        document.body.appendChild(area);
        area.select();
        area.setSelectionRange(0, area.value.length);
        const copied = document.execCommand('copy');
        area.remove();
        return { ok: copied === true, secure: secure, how: 'execCommand' };
    } catch {
        return { ok: false, secure: secure, how: null };
    }
};
