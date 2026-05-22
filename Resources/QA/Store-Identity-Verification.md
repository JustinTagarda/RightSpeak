# Store Identity Verification

Use this checklist before a Microsoft Store submission:

- confirm `RightSpeak.Package\Package.appxmanifest` identity values match the intended Partner Center app identity
- confirm `RightSpeak.Package\Properties\PublishProfiles\PartnerCenter.pubxml` remains x64 and StoreUpload for the package project
- confirm the Premium durable add-on IDs in `App.xaml.cs` match the Partner Center durable add-on
- if Partner Center association export files are available outside the repository, compare them against the manifest before upload

Notes:

- this repository includes the manifest and publish profile, but it does not include Partner Center export metadata
- the final identity association must still be validated in Partner Center before packaging or submission
