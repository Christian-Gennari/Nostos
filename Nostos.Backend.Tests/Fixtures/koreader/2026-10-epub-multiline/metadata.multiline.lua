-- synthetic sidecar: KOReader writes strings with %q, so a highlight that
-- crosses a paragraph break continues on the next line after a backslash
return {
    ["annotations"] = {
        [1] = {
            ["chapter"] = "Chapter One",
            ["datetime"] = "2026-09-28 12:00:00",
            ["note"] = "She said \"wait\" here.",
            ["page"] = "/body/DocFragment[3]/body/p[4]/text().0",
            ["pageno"] = 12,
            ["pos0"] = "/body/DocFragment[3]/body/p[4]/text().0",
            ["pos1"] = "/body/DocFragment[3]/body/p[5]/text().21",
            ["text"] = "The end of one paragraph.\
The start of the next.",
        },
        [2] = {
            ["chapter"] = "Chapter One",
            ["datetime"] = "2026-09-28 12:05:00",
            ["page"] = "/body/DocFragment[3]/body/p[9]/text().0",
            ["pageno"] = 13,
            ["pos0"] = "/body/DocFragment[3]/body/p[9]/text().0",
            ["pos1"] = "/body/DocFragment[3]/body/p[9]/text().18",
            ["text"] = "A later highlight.",
        },
        [3] = {
            ["chapter"] = "Chapter One",
            ["datetime"] = "2026-09-28 12:07:00",
            ["page"] = "/body/DocFragment[3]/body/p[12]/text().0",
            ["pageno"] = 14,
            ["text"] = "in Chapter One",
        },
    },
    ["doc_props"] = {
        ["authors"] = "Ada Reader",
        ["title"] = "Synthetic Reader Book",
    },
}
