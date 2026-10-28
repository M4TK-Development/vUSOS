use tauri::{AppHandle, Manager};
use tauri_plugin_deep_link::DeepLinkExt;

/// Przekazuje deep link `vusos://...` do frontendu przez globalny hook `window.vusosHandleDeepLink(url)`.
fn forward_deep_links(app: &AppHandle, urls: Vec<tauri::Url>) {
    let Some(window) = app.get_webview_window("main") else { return };
    for url in urls {
        let js = format!(
            "window.vusosHandleDeepLink ? window.vusosHandleDeepLink({u}) : (window.__vusosPendingDeepLinks = (window.__vusosPendingDeepLinks || []).concat([{u}]));",
            u = serde_json::to_string(url.as_str()).unwrap_or_else(|_| "\"\"".into())
        );
        let _ = window.eval(&js);
        let _ = window.set_focus();
    }
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, _argv, _cwd| {
            if let Some(w) = app.get_webview_window("main") {
                let _ = w.set_focus();
            }
        }))
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_deep_link::init())
        .setup(|app| {
            #[cfg(any(target_os = "linux", all(debug_assertions, windows)))]
            app.deep_link().register_all()?;

            let handle = app.handle().clone();
            app.deep_link().on_open_url(move |event| {
                forward_deep_links(&handle, event.urls());
            });
            if let Ok(Some(urls)) = app.deep_link().get_current() {
                forward_deep_links(app.handle(), urls);
            }
            Ok(())
        })
        .run(tauri::generate_context!())
        .expect("vUSOS desktop: błąd uruchomienia");
}
