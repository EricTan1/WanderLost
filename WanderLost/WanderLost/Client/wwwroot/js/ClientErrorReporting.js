let defaultLog = console.error;

console.error = async function (e) {
    defaultLog(e);
    try {
        let object = typeof(e.stack) === 'string' ? e.stack : e.toString();
        await fetch('/ClientErrorReporting', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(object)
        });
    }
    catch (e) {
        defaultLog(e);
    }
}