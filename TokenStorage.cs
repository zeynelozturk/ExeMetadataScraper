using System;
using Windows.Security.Credentials;

public class TokenStorage
{
    public const string ResourceName = "ExeMetaDataExtractor";

    public void SaveToken(string token)
    {
        var vault = new PasswordVault();

        if (string.IsNullOrEmpty(token))
        {
            // Remove the existing credential if the token is null or empty
            try
            {
                var credential = vault.Retrieve(ResourceName, "JWT");
                vault.Remove(credential);
            }
            catch (Exception)
            {
                // Ignore if no credential exists
            }
        }
        else
        {
            // Add the new token
            vault.Add(new PasswordCredential(ResourceName, "JWT", token));
        }
    }

    public string GetToken()
    {
        var vault = new PasswordVault();
        var credential = vault.Retrieve(ResourceName, "JWT");
        return credential.Password;
    }



}
