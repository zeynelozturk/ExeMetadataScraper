namespace WinUIMetadataScraper
{
    internal static class ApiRoutes
    {
        public static string GetBaseUrl()
        {
#if DEBUG
            return "https://localhost:5597";
#else
            return "https://defkey.com";
#endif
        }

        public static string GetUploadMetadataUrl() => $"{GetBaseUrl()}/api/exe-lookup/upload-metadata";

        public static string GetDisplayNameUrl() => $"{GetBaseUrl()}/api/auth/get-user-display-name";

    }
}
