// Saves text as a file in the browser (used for CSV exports).
window.copilotBudget = {
    download(fileName, contentType, text) {
        const blob = new Blob([text], { type: contentType });
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
};
