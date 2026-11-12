@echo off
cd /d "%~dp0"
python -c "import cv2, mediapipe, pyvirtualcam, PIL" >nul 2>&1
if errorlevel 1 (
    echo Installing AlapCamera dependencies...
    python -m pip install -r requirements.txt
)
python main.py
if errorlevel 1 pause
