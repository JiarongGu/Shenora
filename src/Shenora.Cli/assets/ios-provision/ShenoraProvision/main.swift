// Nothing here is meant to run. This target exists so `xcodebuild -allowProvisioningUpdates` has an iOS
// app to resolve signing for, which is the only way Apple's toolchain will register an App ID and download
// a provisioning profile. The profile is the artefact; the binary is a by-product.
print("shenora-provision")
