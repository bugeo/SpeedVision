@echo off
rem Riporta SpeedVision all'ultima versione pubblicata su GitHub.
rem Prima salva tutto quello che non e' pubblicato (commit locali e modifiche), cosi' si puo' recuperare.
cd /d "%~dp0"
git rev-parse --abbrev-ref --symbolic-full-name @{u} >nul 2>&1 || (echo Ramo senza collegamento a GitHub: niente da fare. & pause & exit /b 1)
echo Scarico lo stato di GitHub...
git fetch || (echo git fetch non riuscito. & pause & exit /b 1)
echo Salvo i commit locali non pubblicati nel ramo "prima-del-ripristino"...
git branch -f prima-del-ripristino HEAD
echo Salvo le modifiche non salvate (anche i file nuovi) in "git stash"...
git stash push --include-untracked -m "prima del ripristino %date% %time%" -- . ":(exclude)ripristina_github.cmd"
echo Riporto i file all'ultima versione pubblicata...
git reset --hard @{u}
git clean -fd -e ripristina_github.cmd
echo.
echo Fatto. Il programma e' ora come su GitHub. Puoi cancellare questo file.
echo Per recuperare le modifiche: "git stash list" e "git stash apply"; i commit locali sono nel ramo prima-del-ripristino.
pause
