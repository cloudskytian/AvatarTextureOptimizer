#!/usr/bin/env python3
"""ATO brace/paren balance checker.
ATO 括号平衡检查器：剥离字符串/字符/注释后校验 {} () [] 配平。
Not a parser — a smoke gate. Catches truncation & gross structural errors.
"""
import re, sys, glob, os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

def strip_cs(src):
    out = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        # line comment
        if c == '/' and i + 1 < n and src[i+1] == '/':
            j = src.find('\n', i)
            i = n if j < 0 else j
            continue
        # block comment
        if c == '/' and i + 1 < n and src[i+1] == '*':
            j = src.find('*/', i + 2)
            if j < 0:
                return None, 'unterminated /* comment at %d' % i
            i = j + 2
            continue
        # verbatim/interpolated-verbatim string @"..." / $@"..."
        if (c == '@' or c == '$') and i + 1 < n and src[i+1] == '"' or \
           (c == '$' and i + 2 < n and src[i+1] == '@' and src[i+2] == '"') or \
           (c == '@' and i + 2 < n and src[i+1] == '$' and src[i+2] == '"'):
            k = i + 1
            while k < n and src[k] != '"':
                k += 1
            # now at opening quote; verbatim strings escape with ""
            k += 1
            while k < n:
                if src[k] == '"':
                    if k + 1 < n and src[k+1] == '"':
                        k += 2; continue
                    break
                k += 1
            i = k + 1
            continue
        # raw string """ (C# 11) — count quotes
        if src.startswith('"""', i):
            k = i + 3
            j = src.find('"""', k)
            if j < 0:
                return None, 'unterminated raw string at %d' % i
            i = j + 3
            continue
        # char literal
        if c == "'":
            k = i + 1
            while k < n:
                if src[k] == '\\':
                    k += 2; continue
                if src[k] == "'":
                    break
                k += 1
            i = k + 1
            continue
        # regular string (incl. $"...")
        if c == '"' or (c == '$' and i + 1 < n and src[i+1] == '"'):
            k = i + (2 if src[i+1] == '"' and c == '$' else 1)
            while k < n:
                if src[k] == '\\':
                    k += 2; continue
                if src[k] == '"':
                    break
                k += 1
            i = k + 1
            continue
        out.append(c)
        i += 1
    return ''.join(out), None

def check(path, pairs):
    with open(path, encoding='utf-8') as f:
        src = f.read()
    stripped, err = strip_cs(src)
    if err:
        return ['%s: %s' % (path, err)]
    stack = []
    issues = []
    openers = {o: c for o, c in pairs}
    closers = {c: o for o, c in pairs}
    line = 1
    for ch in stripped:
        if ch == '\n':
            line += 1
        elif ch in openers:
            stack.append((ch, line))
        elif ch in closers:
            if not stack or stack[-1][0] != closers[ch]:
                issues.append('%s: unbalanced %r at line %d' % (path, ch, line));
                if len(issues) > 5: break
            else:
                stack.pop()
    if len(issues) <= 5:
        for ch, ln in stack[:5]:
            issues.append('%s: unclosed %r from line %d' % (path, ch, ln))
    return issues

def main():
    pairs_cs = [('{','}'), ('(',')'), ('[',']')]
    issues = []
    files = glob.glob(os.path.join(ROOT, 'Packages', '**', '*.cs'), recursive=True) + \
            glob.glob(os.path.join(ROOT, 'Packages', '**', '*.shader'), recursive=True)
    files = [f for f in files if '/_refs/' not in f and '_refs' not in f]
    for f in sorted(files):
        issues.extend(check(f, pairs_cs))
    import json
    jfiles = [f for f in glob.glob(os.path.join(ROOT, '**', '*.json'), recursive=True)
              if '/_refs/' not in f and 'node_modules' not in f]
    for f in sorted(jfiles):
        try:
            with open(f, encoding='utf-8') as fh: json.load(fh)
        except Exception as e:
            issues.append('%s: JSON invalid: %s' % (f, e))
    print('checked %d cs/shader + %d json files' % (len(files), len(jfiles)))
    if issues:
        print('ISSUES:')
        for i in issues: print('  ' + i)
        sys.exit(1)
    print('OK: all balanced / 全部配平')

if __name__ == '__main__':
    main()
