fn main() {
    // En release, l'exe demande les droits administrateur au lancement :
    // ils sont nécessaires pour lire les FPS (ETW) et la température CPU (WMI).
    // En debug (ou avec GAMEMONITOR_NO_ADMIN=1), on reste en asInvoker pour tester sans UAC.
    println!("cargo:rerun-if-env-changed=GAMEMONITOR_NO_ADMIN");
    let release = std::env::var("PROFILE").as_deref() == Ok("release");
    let no_admin = std::env::var_os("GAMEMONITOR_NO_ADMIN").is_some();
    if release && !no_admin {
        println!("cargo:rustc-link-arg-bins=/MANIFEST:EMBED");
        println!("cargo:rustc-link-arg-bins=/MANIFESTUAC:level='requireAdministrator' uiAccess='false'");
    }
}
