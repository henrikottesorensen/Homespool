// The site's own scripts. wwwroot/lib is LibMan's vendored copies, which are not ours to restyle.
//
// Every rule is an error, since the build treats a problem as a failure either way. A global the
// scripts rely on from a vendored library is declared with a /* global */ comment in the script that
// uses it, not here, so the dependency is stated where it is taken.
import js from '@eslint/js';
import stylistic from '@stylistic/eslint-plugin';
import globals from 'globals';

export default [
    {
        files: ['wwwroot/js/**/*.js'],
        ...js.configs.recommended,
    },
    {
        files: ['wwwroot/js/**/*.js'],
        languageOptions: {
            // Classic scripts, each loaded by its own <script> tag, running in a browser.
            sourceType: 'script',
            globals: {
                ...globals.browser,
            },
        },
        plugins: {
            '@stylistic': stylistic,
        },
        rules: {
            // A script is not a module, so anything declared at its top level lands on window and
            // collides with the next script's. Each one wraps itself in a function instead.
            'no-implicit-globals': 'error',
            'strict': ['error', 'function'],

            // Nothing turns a string into code. The same line the Content-Security-Policy holds.
            'no-eval': 'error',
            'no-implied-eval': 'error',
            'no-new-func': 'error',
            'no-script-url': 'error',

            'consistent-return': 'error',
            'curly': 'error',
            'default-case': 'error',
            'eqeqeq': 'error',
            'no-shadow': 'error',

            // let and const, never var: block scope, and a binding that is never reassigned says so.
            'no-var': 'error',
            'prefer-const': 'error',

            // Layout. The 132 is .editorconfig's max_line_length.
            '@stylistic/brace-style': 'error',

            // Multi-line array and object literals end in a comma, as SA1413 requires of C#'s
            // initializers; argument and parameter lists do not, which SA1413 leaves alone too.
            '@stylistic/comma-dangle': ['error', {
                arrays: 'always-multiline',
                objects: 'always-multiline',
                imports: 'always-multiline',
                exports: 'always-multiline',
                functions: 'never',
            }],

            '@stylistic/eol-last': 'error',
            '@stylistic/indent': ['error', 4],
            '@stylistic/keyword-spacing': 'error',
            '@stylistic/max-len': ['error', { code: 132 }],
            '@stylistic/no-multiple-empty-lines': ['error', { max: 1 }],
            '@stylistic/no-trailing-spaces': 'error',
            '@stylistic/object-curly-spacing': ['error', 'always'],

            // Double quotes, as C#, JSON and Razor use around them; JavaScript has no character
            // literal for single quotes to mean. avoidEscape keeps a selector carrying an HTML
            // attribute - input[name="x"] - in single quotes rather than backslashes.
            '@stylistic/quotes': ['error', 'double', { avoidEscape: true }],
            '@stylistic/semi': ['error', 'always'],
            '@stylistic/space-before-function-paren': ['error', { anonymous: 'always', named: 'never', asyncArrow: 'always' }],

            // Binary operators trail the line they belong to - the rule HS0002 enforces for C#. The
            // stylistic plugin's rather than ESLint's own: that one is deprecated and goes in ESLint
            // 11, and its defaults exempt ? and :, which this is meant to cover.
            '@stylistic/operator-linebreak': ['error', 'after'],
        },
    },
];
