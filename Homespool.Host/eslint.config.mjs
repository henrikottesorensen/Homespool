// The site's own scripts. wwwroot/lib is LibMan's vendored copies, which are not ours to restyle.
//
// @stylistic's operator-linebreak rather than ESLint's own: the core rule is deprecated and goes in
// ESLint 11, and its defaults exempt ? and :, which this rule is meant to cover.
import stylistic from '@stylistic/eslint-plugin';

export default [
    {
        files: ['wwwroot/js/**/*.js'],
        languageOptions: {
            sourceType: 'script',
        },
        plugins: {
            '@stylistic': stylistic,
        },
        rules: {
            // Binary operators trail the line they belong to - the rule HS0002 enforces for C#.
            '@stylistic/operator-linebreak': ['error', 'after'],
        },
    },
];
