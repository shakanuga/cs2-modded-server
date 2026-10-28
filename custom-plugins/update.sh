#!/usr/bin/env bash
# Aktualisiert die eigenen Plugins aus custom-plugins/plugins.json auf die neueste
# GitHub-Release. Läuft getrennt vom kus-Updater (scripts/update/), damit ein
# "Sync fork" mit kus/cs2-modded-server keine Konflikte in dessen Dateien erzeugt.
#
#   ./custom-plugins/update.sh            Updates anwenden, ein Commit pro Plugin
#   ./custom-plugins/update.sh --dry-run  nur anzeigen, was passieren würde
#
# Regeln:
#   - Ein Plugin-Ordner wird nur ersetzt, wenn er im Repo schon existiert.
#     Neu installiert wird nichts; dafür den Ordner einmal von Hand anlegen.
#   - Configs unter configs/ und cfg/ werden nie angefasst.
#   - Schlägt ein Plugin fehl, wird es zurückgesetzt und das nächste geprüft.
#     Exit-Code 1, wenn mindestens eins fehlgeschlagen ist.
# Benötigt: bash, git, curl, jq, unzip. GITHUB_TOKEN wird genutzt, falls gesetzt.

set -euo pipefail

cd "$(git rev-parse --show-toplevel)"
CONFIG="custom-plugins/plugins.json"
DRY_RUN=0
[ "${1:-}" = "--dry-run" ] && DRY_RUN=1

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

AUTH=()
[ -n "${GITHUB_TOKEN:-}" ] && AUTH=(-H "Authorization: Bearer $GITHUB_TOKEN")

api() {
    curl -fsSL "${AUTH[@]}" -H "Accept: application/vnd.github+json" "https://api.github.com/$1"
}

if [ "$DRY_RUN" = 0 ] && [ -n "$(git status --porcelain)" ]; then
    echo "Arbeitsverzeichnis ist nicht sauber, erst committen oder stashen." >&2
    exit 1
fi

failed=()
updated=()
summary=()

count=$(jq '.plugins | length' "$CONFIG")
for ((i = 0; i < count; i++)); do
    p=$(jq -c ".plugins[$i]" "$CONFIG")
    name=$(jq -r .name <<<"$p")
    repo=$(jq -r .repo <<<"$p")
    current=$(jq -r .version <<<"$p")

    echo
    echo "=== $name ($repo)"

    # Welche Ordner sind im Repo installiert?
    targets=()
    archives=()
    keeps=()
    ndirs=$(jq '.dirs | length' <<<"$p")
    for ((d = 0; d < ndirs; d++)); do
        # "marker": Ordner im Archiv über eine Datei darin finden (z. B. die Plugin-DLL),
        # für Releases, deren Ordnername im ZIP wechselt. Sonst über "archive" (Pfadende).
        archive=$(jq -r ".dirs[$d] | if .marker then \"marker:\" + .marker else .archive end" <<<"$p")
        target=""
        while IFS= read -r cand; do
            if [ -d "$cand" ] || [ -f "$cand" ]; then target="$cand"; break; fi
        done < <(jq -r ".dirs[$d].repo[]" <<<"$p")
        if [ -n "$target" ]; then
            targets+=("$target")
            archives+=("$archive")
            keeps+=("$(jq -r ".dirs[$d].keep // [] | join(\"\\n\")" <<<"$p")")
        fi
    done
    if [ ${#targets[@]} -eq 0 ]; then
        echo "nicht im Repo installiert, übersprungen"
        summary+=("--  $name: nicht installiert")
        continue
    fi

    if ! release=$(api "repos/$repo/releases/latest"); then
        echo "FEHLER: konnte die neueste Release nicht abrufen" >&2
        failed+=("$name"); summary+=("!!  $name: Release nicht abrufbar")
        continue
    fi
    tag=$(jq -r .tag_name <<<"$release")
    latest=${tag#[vV]}
    # Tags ohne Ziffern (z. B. "release", immer wieder neu vergeben): Datum mitnehmen,
    # damit eine neue Release trotzdem erkannt wird.
    case "$latest" in *[0-9]*) ;; *) latest="$latest@$(jq -r .published_at <<<"$release")" ;; esac

    if [ "$latest" = "$current" ]; then
        echo "aktuell ($current)"
        summary+=("OK  $name $current")
        continue
    fi
    # Nie auf eine ältere Version zurückgehen (z. B. wenn im Repo schon eine
    # neuere liegt als die neueste Release). Nur bei echten Versionsnummern.
    case "$latest$current" in *@*) ;; *)
        if [ -n "$current" ] && [ "$(printf '%s\n%s\n' "$current" "$latest" | sort -V | tail -1)" = "$current" ]; then
            echo "im Repo ist $current, neueste Release ist $latest - kein Downgrade"
            summary+=("OK  $name $current (Release $latest ist älter)")
            continue
        fi ;;
    esac
    echo "Update: ${current:-unbekannt} > $latest"

    asset=$(jq -r .asset <<<"$p")
    asset=${asset//\{version\}/$latest}
    # "*" im Dateinamen ist ein Platzhalter; es muss genau eine Datei passen.
    re=$(printf '%s' "$asset" | sed -e 's/[.[\^$+?(){}|]/\\&/g' -e 's/\*/.*/g')
    url=$(jq -r --arg re "^${re}$" '[.assets[] | select(.name | test($re))] | if length == 1 then .[0].url else empty end' <<<"$release")
    if [ -z "$url" ]; then
        echo "FEHLER: in Release $tag passt nicht genau eine Datei auf '$asset'. Vorhanden:" >&2
        jq -r '.assets[].name' <<<"$release" | sed 's/^/  /' >&2
        failed+=("$name"); summary+=("!!  $name: Datei $asset fehlt in $tag")
        continue
    fi

    if [ "$DRY_RUN" = 1 ]; then
        echo "[dry-run] würde $asset laden und ersetzen:"
        printf '  %s\n' "${targets[@]}"
        summary+=("UP  $name ${current:-?} > $latest (dry-run)")
        continue
    fi

    dir="$WORK/$i"
    mkdir -p "$dir/x"
    if ! curl -fsSL "${AUTH[@]}" -H "Accept: application/octet-stream" -o "$dir/a.zip" "$url" ||
       ! unzip -q "$dir/a.zip" -d "$dir/x"; then
        echo "FEHLER: Download oder Entpacken fehlgeschlagen" >&2
        failed+=("$name"); summary+=("!!  $name: Download fehlgeschlagen")
        continue
    fi

    ok=1
    for ((k = 0; k < ${#targets[@]}; k++)); do
        archive=${archives[$k]}
        target=${targets[$k]}
        case "$archive" in
            marker:*) mapfile -t found < <(find "$dir/x" -type f -name "${archive#marker:}" -exec dirname {} \;) ;;
            *)        if [ -f "$target" ]; then
                          # Einzelne Datei (z. B. gamedata/*.json) statt Ordner
                          mapfile -t found < <(find "$dir/x" -type f -path "*/$archive")
                      else
                          mapfile -t found < <(find "$dir/x" -type d -path "*/$archive")
                      fi ;;
        esac
        if [ ${#found[@]} -ne 1 ]; then
            echo "FEHLER: '$archive' kommt ${#found[@]}x im Archiv vor (erwartet 1). Hat sich der Aufbau geändert?" >&2
            ok=0; break
        fi
        echo "ersetze $target"
        if [ -f "$target" ]; then
            if ! cp "${found[0]}" "$target"; then
                echo "FEHLER: Kopieren nach $target fehlgeschlagen" >&2
                ok=0; break
            fi
            continue
        fi
        # "keep": Dateien im Repo-Ordner, die nicht überschrieben werden (eigene Configs).
        keepdir="$dir/keep/$k"
        mkdir -p "$keepdir"
        while IFS= read -r kf; do
            [ -n "$kf" ] && [ -f "$target/$kf" ] && mkdir -p "$keepdir/$(dirname "$kf")" && cp -a "$target/$kf" "$keepdir/$kf"
        done <<<"${keeps[$k]}"
        if ! { rm -rf "$target" && cp -a "${found[0]}" "$target" && cp -a "$keepdir/." "$target/"; }; then
            echo "FEHLER: Kopieren nach $target fehlgeschlagen" >&2
            ok=0; break
        fi
        find "$target" \( -name '._*' -o -name '.DS_Store' \) -delete
    done

    # Sicherheitsnetz: nichts außerhalb der Plugin-Ordner darf sich geändert haben.
    if [ "$ok" = 1 ]; then
        while IFS= read -r changed; do
            inside=0
            for t in "${targets[@]}"; do
                case "$changed" in "$t"/*|"$t") inside=1 ;; esac
            done
            if [ "$inside" = 0 ]; then
                echo "FEHLER: unerwartete Änderung außerhalb der Plugin-Ordner: $changed" >&2
                ok=0
            fi
        done < <(git status --porcelain --untracked-files=all | cut -c4-)
    fi

    if [ "$ok" = 0 ]; then
        git checkout -q -- . && git clean -fdq -- "${targets[@]}"
        failed+=("$name"); summary+=("!!  $name: Update abgebrochen, nichts geändert")
        continue
    fi

    tmp=$(mktemp)
    jq --indent 2 --arg v "$latest" ".plugins[$i].version = \$v" "$CONFIG" > "$tmp" && mv "$tmp" "$CONFIG"
    git add -A -- "${targets[@]}" "$CONFIG"
    git commit -q -m "- UPDATED (custom): $name ${current:-?} > $latest"
    updated+=("$name")
    summary+=("UP  $name ${current:-?} > $latest")
done

echo
echo "========== summary"
printf '%s\n' "${summary[@]}"
[ ${#failed[@]} -eq 0 ] || exit 1
