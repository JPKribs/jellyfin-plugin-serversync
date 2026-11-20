// serversync_statistics.js
// What the last scans of each sync type found, as one chart per sync type that is on, and the link
// streams this server started for each server that links to it on each of the last thirty days. The page only reads.

import * as serverSyncModule from './configurationpage?name=serversync_shared.js';

// TYPE_TITLES
// The string key of each sync type's heading, by the key the Statistics route answers with.
var TYPE_TITLES = { Content: 'SyncTypeContent', History: 'SyncTypeHistory', Metadata: 'LabelMetadata', People: 'People', User: 'HeaderUsers' };

// runLabel
// Writes when a scan finished as a short label in the browser's own time: the time for a scan today, such
// as "14:05", and the day for an older one, such as "Nov 15". A short label fits under a narrow group of bars.
//
// Inputs
// finishedAt [string]: The time the route answered with, in UTC.
// now [Date]: The current time, or nothing for the clock.
//
// Output
// [string]: The label, or an empty string for a time that cannot be read.
export function runLabel(finishedAt, now) {
    var when = new Date(finishedAt);
    if (isNaN(when.getTime())) return '';
    var today = now || new Date();
    var sameDay = when.getFullYear() === today.getFullYear() && when.getMonth() === today.getMonth() && when.getDate() === today.getDate();
    return sameDay
        ? when.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })
        : when.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

// countAxisMax
// Picks the top of a chart's axis so its gridlines fall on whole numbers when the counts are small.
//
// Inputs
// series [object[]]: The chart's series, each with values.
//
// Output
// [number|undefined]: 4 while no value is above it, or nothing to let the chart choose.
export function countAxisMax(series) {
    var highest = 0;
    (series || []).forEach(function (one) {
        (one.values || []).forEach(function (value) { if (value > highest) highest = value; });
    });
    return highest <= 4 ? 4 : undefined;
}

// dayLabel
// Writes a day as a short label, such as "Nov 16". The day is this server's own, so it is read without a time zone.
//
// Inputs
// day [string]: The day as yyyy-MM-dd.
//
// Output
// [string]: The label, or the input for a day that cannot be read.
export function dayLabel(day) {
    var parts = String(day || '').split('-').map(Number);
    if (parts.length !== 3 || parts.some(isNaN)) return String(day || '');
    return new Date(parts[0], parts[1] - 1, parts[2]).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

// scanChartData
// Turns a sync type's scans into the labels and the three series of its chart.
//
// Inputs
// runs [object[]]: The scans, oldest first, each with FinishedAt, Added, Updated, and Removed.
// t [object]: The strings handle that names the series.
//
// Output
// [object]: labels and series for a Bar Chart.
export function scanChartData(runs, t) {
    var list = runs || [];
    return {
        labels: list.map(function (run) { return runLabel(run.FinishedAt); }),
        series: [
            { name: t.tr('New'), color: 'green', values: list.map(function (run) { return run.Added || 0; }) },
            { name: t.tr('ChartUpdated'), color: 'blue', values: list.map(function (run) { return run.Updated || 0; }) },
            { name: t.tr('ChartRemoved'), color: 'red', values: list.map(function (run) { return run.Removed || 0; }) }
        ]
    };
}

// streamChartData
// Turns the link streams of each server into the labels and the series of their chart, one line for each server.
//
// Inputs
// linkStreams [object]: Days, oldest first, and Servers, each with Name and the Streams of every day.
// t [object]: The strings handle that names a server with no name.
//
// Output
// [object]: labels and series for a Line Chart.
export function streamChartData(linkStreams, t) {
    var days = (linkStreams && linkStreams.Days) || [];
    var servers = (linkStreams && linkStreams.Servers) || [];
    return {
        labels: days.map(dayLabel),
        series: servers.map(function (server) {
            var streams = server.Streams || [];
            return { name: server.Name || server.Key || t.tr('TabServer'), values: days.map(function (day, index) { return streams[index] || 0; }) };
        })
    };
}

// serversync_statistics
// Builds the Statistics tab. Each time the page shows, it reads the Statistics route and draws one
// section for each sync type that is on.
//
// Inputs
// view [HTMLElement]: The page the dashboard passes to the controller.
export default function (view) {
    'use strict';

    var apiRequest, setVisible, getEl;

    // The charts on screen, kept so each can stop watching its width when the page redraws or hides.
    var charts = [];

    var made = serverSyncModule.createServerSyncPage(view, { page: 'serversync_statistics', load: function () { loadStatistics(); }, onHide: function () { clearCharts(); } });
    ({ apiRequest, setVisible, getEl } = made.shared.pageHelpers());

    // clearCharts
    // Destroys every chart on screen and empties the sections.
    function clearCharts() {
        charts.forEach(function (chart) { chart.destroy(); });
        charts = [];
        var holder = getEl('statisticsSections');
        if (holder) holder.innerHTML = '';
    }

    // addSection
    // Adds an always open section to the page and returns the element its charts go in.
    //
    // Inputs
    // title [string]: The section's heading.
    //
    // Output
    // [HTMLElement]: The section's content.
    function addSection(title) {
        var section = document.createElement('div');
        section.className = 'base-collapsible-section fixed';
        var header = document.createElement('div');
        header.className = 'base-collapsible-header';
        var heading = document.createElement('span');
        heading.className = 'base-collapsible-title';
        heading.textContent = title;
        header.appendChild(heading);
        var content = document.createElement('div');
        content.className = 'base-collapsible-content';
        section.appendChild(header);
        section.appendChild(content);
        getEl('statisticsSections').appendChild(section);
        return content;
    }

    // addChart
    // Puts a chart in a section and keeps it for cleanup.
    //
    // Inputs
    // content [HTMLElement]: The section's content.
    // chart [object]: The chart handle.
    function addChart(content, chart) {
        charts.push(chart);
        content.appendChild(chart.element);
    }

    // addLinkStreams
    // Adds the Link Streams chart to a section, with a line for each server that may link to this one.
    //
    // Inputs
    // content [HTMLElement]: The section's content.
    // linkStreams [object]: Days and Servers, as the route answers.
    // heading [boolean]: Whether to head the chart, which a section of its own does not need.
    function addLinkStreams(content, linkStreams, heading) {
        if (heading) {
            var title = document.createElement('div');
            title.className = 'base-subsection-title';
            title.textContent = made.shared.t.tr('HeaderLinkStreams');
            content.appendChild(title);
        }
        var note = document.createElement('div');
        note.className = 'fieldDescription serversync-mb-md';
        note.textContent = made.shared.t.tr('LinkStreamsDescription');
        content.appendChild(note);
        var streams = streamChartData(linkStreams, made.shared.t);
        addChart(content, serverSyncModule.createLineChart({
            labels: streams.labels,
            series: streams.series,
            max: countAxisMax(streams.series),
            label: made.shared.t.tr('ChartLinkStreamsLabel'),
            emptyText: made.shared.t.tr('ChartLinkStreamsEmpty')
        }));
    }

    // drawStatistics
    // Draws one section for each sync type the route answered with. The link streams go under Content, or in
    // a section of their own on a server that has content sync off and still serves links. The page's
    // description shows only while a sync type is on, since the Link Streams section describes itself.
    //
    // Inputs
    // statistics [object]: The route's answer, with Types and LinkStreams.
    function drawStatistics(statistics) {
        clearCharts();
        var types = (statistics && statistics.Types) || [];
        var linkStreams = statistics && statistics.LinkStreams;
        var hasContent = types.some(function (type) { return type.Key === 'Content'; });
        setVisible('statisticsIntro', types.length > 0);
        setVisible('statisticsDisabled', types.length === 0 && !linkStreams);
        types.forEach(function (type) {
            var title = TYPE_TITLES[type.Key] ? made.shared.t.tr(TYPE_TITLES[type.Key]) : type.Key;
            var content = addSection(title);
            var scans = scanChartData(type.Runs, made.shared.t);
            addChart(content, serverSyncModule.createBarChart({
                labels: scans.labels,
                series: scans.series,
                max: countAxisMax(scans.series),
                label: made.shared.t.format('ChartScansLabel', title),
                emptyText: made.shared.t.tr('ChartScansEmpty')
            }));
            if (type.Key === 'Content' && linkStreams) addLinkStreams(content, linkStreams, true);
        });
        if (linkStreams && !hasContent) addLinkStreams(addSection(made.shared.t.tr('HeaderLinkStreams')), linkStreams, false);
    }

    // loadStatistics
    // Reads the Statistics route and draws it. A failed read says so and leaves the page empty.
    function loadStatistics() {
        var status = getEl('statisticsStatus');
        if (status) status.textContent = '';
        apiRequest('Statistics', 'GET').then(drawStatistics).catch(function () {
            clearCharts();
            setVisible('statisticsIntro', false);
            setVisible('statisticsDisabled', false);
            if (status) status.textContent = made.shared.t.tr('ErrorStatisticsLoad');
        });
    }
}
