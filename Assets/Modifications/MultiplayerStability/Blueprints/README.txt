Release-package placeholder for the required Blueprints folder. Owlcat's build pipeline excludes
this text file, so packaging must copy it into Build/MultiplayerStability/Blueprints before creating
the ZIP. Without a non-empty folder, some installers omit Blueprints and OwlcatModification logs a
DirectoryNotFoundException while applying this otherwise code-only mod.
