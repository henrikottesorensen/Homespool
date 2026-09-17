// Writes each ESLint problem as a canonical MSBuild message - file(line,col): error rule: text - so
// the Exec task running ESLint reports it as a build error with a location, which is what Rider and
// the command line both show. ESLint ships no formatter in that shape.
module.exports = results => results
    .flatMap(result => result.messages.map(message =>
        `${result.filePath}(${message.line ?? 1},${message.column ?? 1}): ` +
        `${message.severity === 2 ? 'error' : 'warning'} ${message.ruleId ?? 'eslint'}: ${message.message}`))
    .join('\n');
