using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SPWare.VirtualSoundCanvas.Config
{
    /// <summary>
    /// 새 버전이 나왔는지 GitHub의 공개 API에서 최신 릴리스 번호만 확인한다(자동 설치는 하지 않는다 - 안내만 한다).
    /// 확인은 화면이 뜬 뒤 백그라운드에서 하고, 5초 안에 답이 없거나 인터넷이 없으면 아무 표시 없이 조용히 포기한다.
    /// 보내는 것은 요청 자체(IP 주소는 GitHub에 보인다)뿐이고 개인 정보나 설정은 보내지 않는다.
    /// </summary>
    public static class UpdateChecker
    {
        public const string Repo = "specialwind4/SPWare-WindSound";
        public const int TimeoutSeconds = 5;

        /// <summary>시험에서 주소를 바꿀 수 있게 열어 둔다.</summary>
        public static string ApiUrl { get; set; } = $"https://api.github.com/repos/{Repo}/releases/latest";

        private static readonly Regex TagPattern = new(@"^v?(\d{1,4})\.(\d{1,4})\.(\d{1,4})$", RegexOptions.Compiled);

        /// <summary>태그(v1.2.3)를 버전으로. 형식이 다르면 null.</summary>
        public static Version? ParseTag(string? tag)
        {
            var m = TagPattern.Match(tag ?? "");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)) : null;
        }

        /// <summary>지금 실행 중인 프로그램의 버전(major.minor.patch).</summary>
        public static Version CurrentVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }

        /// <summary>안내에 쓸 다운로드 페이지 주소. 받아 온 주소를 그대로 쓰지 않고, 검증한 태그로 우리가 직접 만든다.</summary>
        public static string ReleasePageUrl(string tag) => $"https://github.com/{Repo}/releases/tag/{tag}";

        /// <summary>
        /// 최신 릴리스 태그를 돌려준다. 성공하면 (true, 태그), 실패(인터넷 없음/시간 초과/형식 이상)면 (false, null).
        /// 이 메서드는 어떤 경우에도 예외를 던지지 않는다.
        /// </summary>
        public static async Task<(bool ok, string? tag)> GetLatestTagAsync(CancellationToken ct = default)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
                req.Headers.UserAgent.ParseAdd("SPWare-WindSound-UpdateCheck");
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));   // 연결은 됐는데 응답이 늘어지는 경우도 끊는다
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return (false, null);
                if (resp.Content.Headers.ContentLength is long n && n > 512 * 1024) return (false, null);   // 이상하게 큰 응답은 읽지 않는다
                string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                string? tag = doc.RootElement.GetProperty("tag_name").GetString();
                return ParseTag(tag) is null ? (false, null) : (true, tag);
            }
            catch
            {
                return (false, null);
            }
        }

        /// <summary>태그가 지금 버전보다 새것인가.</summary>
        public static bool IsNewer(string? tag, Version current) => ParseTag(tag) is { } v && v > current;
    }
}
