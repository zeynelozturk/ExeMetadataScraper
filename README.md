**What is ExeMetadataScraper?**

ExeMetadataScraper is a helper utility for Defkey.com contributors, designed to extract and 
send .exe file metadata from Windows executable files (.exe). These metadata then shared 
on https://defkey.com/exe for the benefit of the community.

The actual .exe files are never sent to Defkey.com; only the extracted metadata is transmitted.
The advantage of this approach versus uploading the entire .exe file on web interface is that this is much faster and
also allows files bigger than 100MB to be analyzed (no limit).

The disadvantage (versus the web interface) is that this tool only works on Windows 10 and higher.

**How to Use ExeMetadataScraper?**

- You need a defkey.com account to use this tool.
- Download the latest release from the Releases section.
- Click Login button, it'll automatically login if you're logged in your default browser.
- Click Select File button to select a .exe file and click Send.

Now you can check your "Drafts" section on Defkey.com to see the uploaded metadata.
You'll also receive an email notification for each successful upload.

**Why is this app ~200 MB!?**
The executable is published as a self-contained, single-file build using .NET 8 and the Windows App SDK.
It includes its own runtime and UI framework, allowing it to run on Windows 10 and higher without
installation or dependencies.

**Note on Dependencies**  

This project is part of a larger solution and relies on internal components 
that are not publicly available or open source. While the code in this 
repository reflects the core logic and structure of the tool, 
it may not compile or function independently without access to those proprietary dependencies. 

