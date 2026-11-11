//! 실행 시점 체크아웃 상태이며 빌드 소스의 증명은 아니다.
use std::process::Command;

/// GITHUB_SHA takes precedence; otherwise report HEAD and tracked working-tree changes.
/// Returns `unknown` when git cannot identify the checkout or its status.
pub fn commit_label() -> String {
    if let Ok(sha) = std::env::var("GITHUB_SHA") {
        return sha;
    }
    let git = |args: &[&str]| {
        Command::new("git")
            .args(args)
            .current_dir(env!("CARGO_MANIFEST_DIR"))
            .output()
            .ok()
            .filter(|out| out.status.success())
    };
    let Some(head) = git(&["rev-parse", "HEAD"]) else {
        return "unknown".into();
    };
    let Ok(head) = String::from_utf8(head.stdout) else {
        return "unknown".into();
    };
    let head = head.trim();
    if head.len() != 40 || !head.bytes().all(|b| b.is_ascii_hexdigit()) {
        return "unknown".into();
    }
    let Some(status) = git(&["status", "--porcelain", "--untracked-files=no"]) else {
        return "unknown".into();
    };
    format!(
        "{head}{}",
        if status.stdout.is_empty() {
            ""
        } else {
            "-dirty"
        }
    )
}

#[cfg(test)]
mod tests {
    #[test]
    fn commit_format_or_environment_or_unknown() {
        let actual = super::commit_label();
        if let Ok(sha) = std::env::var("GITHUB_SHA") {
            assert_eq!(actual, sha);
        } else if actual != "unknown" {
            let head = actual.strip_suffix("-dirty").unwrap_or(&actual);
            assert_eq!(head.len(), 40);
            assert!(head.bytes().all(|b| b.is_ascii_hexdigit()));
        }
    }
}
