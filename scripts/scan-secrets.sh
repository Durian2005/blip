#!/usr/bin/env bash
# scan-secrets.sh —— 脱敏三查（对应铁律 2 / WBS 外的 A3 工程化任务）
#
# 查什么：
#   1. 工作区（git 跟踪的文件，自动跳过 .gitignore 与 scripts/ 自身）
#   2. 全历史（git rev-list --all 的每一个提交的全部内容，等价于 git log -p 覆盖面）
#   3. 个人模式清单（scripts/scan-patterns.local.txt，gitignore，存真名/用户名/设备型号）
#
# 通用正则（MAC / IPv4 / 本机路径 / 私钥块 / 凭据赋值）写死在本脚本里，随仓库分发；
# 个人特定字符串只放 scan-patterns.local.txt，否则脚本自己就把敏感信息带进库了。
#
# 硬性要求（铁律 3）：逐模式单独跑，**「命令报错」与「没有匹配」走不同分支**——
# 工具报错绝不能被当成"扫描通过"。
#
# 退出码三态：
#   0 = 全部干净（无任何匹配，且工具无报错）
#   1 = 发现可疑内容（有人需要肉眼审）
#   2 = 工具报错（扫描不完整，结果不可信，**不得当通过**）
#
# 用法：scripts/scan-secrets.sh   （在仓库任意子目录执行均可）

set -u

REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || {
    echo "[错误] 不在 git 仓库内，无法扫描。" >&2
    exit 2
}
cd "$REPO_ROOT"

# 本脚本与示例文件自身包含正则原文，会把自己扫出来 —— 一律排除
EXCLUDE_PATHSPEC=(-- . ':(exclude)scripts')

TMPDIR_SCAN="$(mktemp -d)"
trap 'rm -rf "$TMPDIR_SCAN"' EXIT

# ----------------------------------------------------------------------
# 模式表：label | grep 额外 flag（- 表示无）| ERE 正则 | 误报过滤（- 表示无）
# 误报过滤是一条 grep -Ev 正则，对命中的行做白名单放行（如回环地址、占位符）。
# ----------------------------------------------------------------------
LABELS=()
FLAGS=()
RES=()
FILTERS=()

add_pattern() { LABELS+=("$1"); FLAGS+=("$2"); RES+=("$3"); FILTERS+=("$4"); }

add_pattern "MAC 地址"           "-" '([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}' "-"
# IPv4 的白名单：回环/广播地址，以及形似点分四段的版本号（如驱动版本 24.40.10.3）
add_pattern "IPv4 地址"          "-" '(^|[^0-9.])((25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])([^0-9.]|$)' '127\.0\.0\.1|0\.0\.0\.0|255\.255\.255\.255|24\.40\.10\.3'
add_pattern "Windows 本机路径"   "-" 'C:[/\\]Users[/\\][A-Za-z0-9_. -]+' "-"
add_pattern "Unix home 路径"     "-" '/(home|Users)/[a-z0-9_.-]+' "-"
add_pattern "私钥块头"           "-" '-----BEGIN [A-Z ]*PRIVATE KEY( BLOCK)?-----' "-"
add_pattern "凭据赋值(双引号)"   "i" '(password|passwd|secret|token|api[_-]?key|access[_-]?key)[[:space:]]*[=:][[:space:]]*"[^"]{3,}"' '<[^>]*>|\$\{[^}]*\}|xxx|XXXX|example|示例|占位|placeholder'
add_pattern "凭据赋值(单/无引号)" "i" '(password|passwd|secret|token|api[_-]?key|access[_-]?key)[[:space:]]*[=:][[:space:]]*[A-Za-z0-9+/_-]{8,}' '<[^>]*>|\$\{[^}]*\}|xxx|XXXX|example|示例|占位|placeholder'

# ----------------------------------------------------------------------
# 个人模式清单（第 3 查）
# ----------------------------------------------------------------------
LOCAL_FILE="scripts/scan-patterns.local.txt"
LOCAL_LABELS=()
LOCAL_RES=()
if [[ -f "$LOCAL_FILE" ]]; then
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"                       # 兼容 CRLF
        [[ -z "$line" || "$line" == \#* ]] && continue
        LOCAL_LABELS+=("个人: $line")
        LOCAL_RES+=("$line")
    done < "$LOCAL_FILE"
    echo "[清单] 已加载个人模式 ${#LOCAL_RES[@]} 条（$LOCAL_FILE）"
else
    echo "[警告] $LOCAL_FILE 不存在——第 3 查（个人字符串）为空，扫描不完整。" >&2
fi

# rev-list 一次，失败立即退出 2（报错 ≠ 无匹配）
COMMITS="$(git rev-list --all 2>"$TMPDIR_SCAN/revlist.err")" || {
    echo "[错误] git rev-list --all 失败：" >&2
    cat "$TMPDIR_SCAN/revlist.err" >&2
    exit 2
}
[[ -z "$COMMITS" ]] && { echo "[错误] 仓库没有任何提交，历史扫描无法进行。" >&2; exit 2; }

ERROR_COUNT=0
HIT_COUNT=0

# ----------------------------------------------------------------------
# 单模式执行：工作区 + 历史。git grep 退出码：0=有匹配，1=无匹配，>1=报错
# ----------------------------------------------------------------------
run_one() {
    local label="$1" flags="$2" re="$3" filter="$4" scope="$5" raw_file="$6"
    local -a cmd=(git grep -I -n -E -e "$re")
    [[ "$flags" == "i" ]] && cmd+=( -i )
    if [[ "$scope" == "工作区" ]]; then
        cmd+=( "${EXCLUDE_PATHSPEC[@]}" )
    else
        cmd+=( $COMMITS "${EXCLUDE_PATHSPEC[@]}" )
    fi

    "${cmd[@]}" > "$raw_file" 2>"$raw_file.err"
    local rc=$?
    if [[ $rc -gt 1 ]]; then
        echo "[工具错误] [$label] $scope 扫描异常（git grep 退出码 $rc），本模式结果不可信：" >&2
        cat "$raw_file.err" >&2
        return 2
    fi
    return $rc   # 0=有匹配 1=无匹配（注意与整体退出码语义相反，这是 git grep 的约定）
}

for i in "${!RES[@]}"; do
    for scope in 工作区 历史; do
        raw="$TMPDIR_SCAN/raw_${i}_${scope}.txt"
        run_one "${LABELS[$i]}" "${FLAGS[$i]}" "${RES[$i]}" "${FILTERS[$i]}" "$scope" "$raw"
        rc=$?
        if [[ $rc -eq 2 ]]; then ERROR_COUNT=$((ERROR_COUNT+1)); continue; fi
        [[ $rc -eq 1 ]] && continue     # 无匹配，走下一个

        # 有原始命中 → 套误报过滤；过滤本身的报错同样单独成支
        if [[ "${FILTERS[$i]}" != "-" ]]; then
            grep -Ev "${FILTERS[$i]}" "$raw" > "$raw.filtered" 2>"$raw.filter.err"
            frc=$?
            if [[ $frc -gt 1 ]]; then
                echo "[工具错误] [$label] $scope 误报过滤异常（grep 退出码 $frc）：" >&2
                cat "$raw.filter.err" >&2
                ERROR_COUNT=$((ERROR_COUNT+1)); continue
            fi
            mv "$raw.filtered" "$raw"
        fi

        if [[ -s "$raw" ]]; then
            HIT_COUNT=$((HIT_COUNT+1))
            n=$(wc -l < "$raw")
            echo "── [${LABELS[$i]}] $scope：$n 处命中"
            cut -c1-200 "$raw"
        fi
    done
done

# 个人模式同样逐条单独跑（只扫工作区与历史，与上面同一套分支语义）
for i in "${!LOCAL_RES[@]}"; do
    for scope in 工作区 历史; do
        raw="$TMPDIR_SCAN/raw_local_${i}_${scope}.txt"
        run_one "${LOCAL_LABELS[$i]}" "-" "${LOCAL_RES[$i]}" "-" "$scope" "$raw"
        rc=$?
        if [[ $rc -eq 2 ]]; then ERROR_COUNT=$((ERROR_COUNT+1)); continue; fi
        [[ $rc -eq 1 ]] && continue
        HIT_COUNT=$((HIT_COUNT+1))
        n=$(wc -l < "$raw")
        echo "── [${LOCAL_LABELS[$i]}] $scope：$n 处命中"
        cut -c1-200 "$raw"
    done
done

echo "────────────────────────────"
if [[ $ERROR_COUNT -gt 0 ]]; then
    echo "结论：$ERROR_COUNT 个模式扫描时报错。扫描不完整，**不能视为通过**。" >&2
    exit 2
fi
if [[ $HIT_COUNT -gt 0 ]]; then
    echo "结论：$HIT_COUNT 个模式有命中，逐条人工审（多为误报，如文档里的示例）后自行判断。"
    exit 1
fi
echo "结论：工作区与全历史均无命中，扫描通过。"
exit 0
