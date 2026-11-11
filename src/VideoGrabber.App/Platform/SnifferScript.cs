namespace VideoGrabber.App.Platform;

/// <summary>
/// 注入到每个页面的嗅探脚本：钩住 fetch / XHR / HTMLMediaElement.src / srcObject /
/// createObjectURL / MutationObserver，把命中的媒体地址通过 postMessage 上报。
/// </summary>
internal static class SnifferScript
{
    public const string Source = """
        (function () {
          if (window.__vgInstalled) return;
          window.__vgInstalled = true;
          var seen = new Set();

          function send(url, via) {
            try {
              if (!url) return;
              var text = String(url);
              if (!text) return;
              var key = text;
              if (seen.has(key)) return;
              seen.add(key);
              if (seen.size > 800) seen.clear();
              window.chrome.webview.postMessage({
                type: 'media',
                url: text,
                via: via,
                page: location.href,
                ua: navigator.userAgent
              });
            } catch (e) { }
          }

          function looksLikeMedia(url) {
            try {
              var text = String(url).toLowerCase();
              if (text.indexOf('blob:') === 0) return true;
              var cut = text.split('#')[0].split('?')[0];
              var exts = ['.m3u8', '.m4u8', '.mp4', '.m4s', '.ts', '.mp3', '.webm', '.m4a', '.mov'];
              for (var i = 0; i < exts.length; i++) {
                if (cut.endsWith(exts[i])) return true;
              }
            } catch (e) { }
            return false;
          }

          function report(url, via) {
            if (!url) return;
            if (!looksLikeMedia(url)) return;
            send(url, via);
          }

          // fetch
          try {
            var originalFetch = window.fetch;
            if (originalFetch) {
              window.fetch = function (input, init) {
                try {
                  var url = typeof input === 'string' ? input : (input && input.url);
                  report(url, 'fetch');
                } catch (e) { }
                return originalFetch.apply(this, arguments);
              };
            }
          } catch (e) { }

          // XMLHttpRequest
          try {
            var originalOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function (method, url) {
              report(url, 'xhr');
              return originalOpen.apply(this, arguments);
            };
          } catch (e) { }

          // HTMLMediaElement.src
          try {
            var srcDescriptor = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'src');
            if (srcDescriptor && srcDescriptor.set) {
              Object.defineProperty(HTMLMediaElement.prototype, 'src', {
                configurable: true,
                get: srcDescriptor.get,
                set: function (value) {
                  report(value, 'media-src');
                  return srcDescriptor.set.call(this, value);
                }
              });
            }
          } catch (e) { }

          // srcObject（MSE）
          try {
            var objectDescriptor = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'srcObject');
            if (objectDescriptor && objectDescriptor.set) {
              Object.defineProperty(HTMLMediaElement.prototype, 'srcObject', {
                configurable: true,
                get: objectDescriptor.get,
                set: function (value) {
                  try { report(this.currentSrc || this.src, 'src-object'); } catch (e) { }
                  return objectDescriptor.set.call(this, value);
                }
              });
            }
          } catch (e) { }

          // URL.createObjectURL（MediaSource → blob:）
          try {
            var originalCreate = URL.createObjectURL;
            URL.createObjectURL = function (object) {
              var result = originalCreate.apply(this, arguments);
              report(result, 'blob');
              return result;
            };
          } catch (e) { }

          // 新出现的 video / audio / source
          function scan(node) {
            try {
              if (!node || !node.querySelectorAll) return;
              node.querySelectorAll('video, audio, source').forEach(function (element) {
                report(element.currentSrc || element.src, 'dom');
              });
            } catch (e) { }
          }

          try {
            var observer = new MutationObserver(function (mutations) {
              mutations.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) { scan(node); });
              });
            });
            observer.observe(document.documentElement, { childList: true, subtree: true });
          } catch (e) { }

          // 播放中 / 元数据就绪时再补报一次，覆盖脚本注入前就已创建的播放器。
          document.addEventListener('loadedmetadata', function (event) {
            try {
              var element = event.target;
              if (element && (element.tagName === 'VIDEO' || element.tagName === 'AUDIO')) {
                report(element.currentSrc || element.src, 'media-event');
              }
            } catch (e) { }
          }, true);
          document.addEventListener('playing', function (event) {
            try {
              var element = event.target;
              if (element && (element.tagName === 'VIDEO' || element.tagName === 'AUDIO')) {
                report(element.currentSrc || element.src, 'media-playing');
              }
            } catch (e) { }
          }, true);

          // 供接管流程调用：静音自动播放 + 点击明显的播放按钮 + 滚动触发懒加载。
          window.__vgNudgeCount = 0;
          window.__vgNudgePlayback = function () {
            try {
              window.__vgNudgeCount++;
              document.querySelectorAll('video').forEach(function (video) {
                try {
                  video.muted = true;
                  video.setAttribute('muted', '');
                  video.setAttribute('playsinline', '');
                  video.autoplay = true;
                  var promise = video.play();
                  if (promise && promise.catch) promise.catch(function () { });
                } catch (e) { }
              });

              if (window.__vgNudgeCount === 1) {
                document.querySelectorAll('video').forEach(function (video) {
                  try { video.scrollIntoView({ block: 'center', inline: 'center' }); } catch (e) { }
                });
              }

              if (window.__vgNudgeCount <= 3) {
                var selectors = [
                  '.vjs-big-play-button',
                  '.plyr__control--overlaid',
                  '.jw-display-icon-container',
                  'button[aria-label*="play" i]',
                  'button[title*="play" i]',
                  '[class*="play-button"]',
                  '[class*="play-btn"]'
                ];
                selectors.forEach(function (selector) {
                  try {
                    var button = document.querySelector(selector);
                    if (button && button.click) button.click();
                  } catch (e) { }
                });
              }

              window.scrollBy(0, Math.max(200, (window.innerHeight || 700) * 0.6));
            } catch (e) { }
          };
        })();
        """;
}
