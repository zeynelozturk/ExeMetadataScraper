using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WinUIMetadataScraper
{
    public class AuthService
    {
        private readonly HttpClient _httpClient;

        public AuthService()
        {
            _httpClient = new HttpClient
            {
                BaseAddress = new Uri(ApiRoutes.GetBaseUrl())
            };
        }

        public async Task<string> LoginAsync(string email, string password)
        {
            var loginData = new LoginRequestDto { Email = email, Password = password };

            // Source-generated serialization (uses AppJsonContext)
            string json = JsonSerializer.Serialize(loginData, AppJsonContext.Default.LoginRequestDto);

            var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); // avoid overload issue

            // DO NOT CHANGE
            var response = await _httpClient.PostAsync($"{ApiRoutes.GetBaseUrl()}/account/login", content);

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("token", out var tokenProp) &&
                    tokenProp.ValueKind == JsonValueKind.String)
                {
                    return tokenProp.GetString()!;
                }
                throw new Exception("Token not found in response");
            }

            throw new Exception("Login failed");
        }

        public async Task<string?> GetUserDisplayNameAsync(string token)
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var response = await _httpClient.GetAsync(ApiRoutes.GetDisplayNameUrl());
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            var dto = await JsonHelpers.ReadDisplayNameAsync(stream);
            return dto?.DisplayName;
        }
    }
}
